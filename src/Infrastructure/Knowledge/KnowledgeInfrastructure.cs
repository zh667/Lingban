using System.ClientModel;
using System.IO.Compression;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Lingban.Application.Common.Interfaces;
using Lingban.Application.Knowledge.Commands;
using Lingban.Domain.Entities.Knowledge;
using Lingban.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using OpenAI;
using Pgvector;

namespace Lingban.Infrastructure.Knowledge;

/// <summary>
/// docx(OpenXml:按文档顺序遍历段落与表格,标题层级栈生成 "H1 > H2" 路径;
/// 打开前做 zip 膨胀防护)与 md/txt(按 # 标题行)。
/// </summary>
public class DocumentParser : IDocumentParser
{
    private const int MaxZipEntries = 500;
    private const long MaxUncompressedBytes = 50 * 1024 * 1024;
    private const long MaxEntryBytes = 20 * 1024 * 1024;

    public IReadOnlyList<DocumentSection> Parse(string fileName, byte[] content)
    {
        return fileName.EndsWith(".docx", StringComparison.OrdinalIgnoreCase)
            ? ParseDocx(content)
            : ParseText(Encoding.UTF8.GetString(content));
    }

    private static IReadOnlyList<DocumentSection> ParseDocx(byte[] content)
    {
        GuardAgainstZipBombs(content);

        using var stream = new MemoryStream(content);
        using WordprocessingDocument word = WordprocessingDocument.Open(stream, false);
        Body body = word.MainDocumentPart?.Document?.Body
            ?? throw new InvalidOperationException("docx 缺少文档主体。");

        var sections = new List<DocumentSection>();
        var current = new StringBuilder();
        var headingStack = new List<(int Level, string Text)>();

        void FlushSection()
        {
            string path = string.Join(" > ", headingStack.Select(entry => entry.Text));
            Flush(sections, path, current);
        }

        foreach (OpenXmlElement element in body.ChildElements)
        {
            switch (element)
            {
                case Paragraph paragraph:
                    string text = paragraph.InnerText.Trim();
                    if (text.Length == 0)
                    {
                        continue;
                    }

                    int? level = HeadingLevel(paragraph);
                    if (level is int headingLevel)
                    {
                        FlushSection();
                        headingStack.RemoveAll(entry => entry.Level >= headingLevel);
                        headingStack.Add((headingLevel, text));
                    }
                    else
                    {
                        current.AppendLine(text);
                    }

                    break;

                case Table table:
                    // 表格按行拼接(单元格以制表符分隔),点检表/参数表不丢失(七审 #9)。
                    foreach (TableRow row in table.Elements<TableRow>())
                    {
                        string rowText = string.Join('\t', row.Elements<TableCell>()
                            .Select(cell => cell.InnerText.Trim())
                            .Where(cellText => cellText.Length > 0));
                        if (rowText.Length > 0)
                        {
                            current.AppendLine(rowText);
                        }
                    }

                    break;
            }
        }

        FlushSection();
        return sections;
    }

    private static int? HeadingLevel(Paragraph paragraph)
    {
        string? style = paragraph.ParagraphProperties?.ParagraphStyleId?.Val?.Value;
        if (style is null)
        {
            return null;
        }

        if (style.StartsWith("Heading", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(style["Heading".Length..], out int level))
        {
            return level;
        }

        return int.TryParse(style, out int numeric) && numeric is >= 1 and <= 9 ? numeric : null;
    }

    private static void GuardAgainstZipBombs(byte[] content)
    {
        using var stream = new MemoryStream(content);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        if (archive.Entries.Count > MaxZipEntries)
        {
            throw new InvalidOperationException("docx 条目数超出限制。");
        }

        long total = 0;
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            if (entry.Length > MaxEntryBytes)
            {
                throw new InvalidOperationException("docx 单条目解压尺寸超出限制。");
            }

            total += entry.Length;
        }

        if (total > MaxUncompressedBytes)
        {
            throw new InvalidOperationException("docx 解压总尺寸超出限制。");
        }
    }

    private static IReadOnlyList<DocumentSection> ParseText(string text)
    {
        var sections = new List<DocumentSection>();
        var current = new StringBuilder();
        string heading = string.Empty;
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.TrimEnd();
            if (line.StartsWith('#'))
            {
                Flush(sections, heading, current);
                heading = line.TrimStart('#', ' ');
            }
            else
            {
                current.AppendLine(line);
            }
        }

        Flush(sections, heading, current);
        return sections;
    }

    private static void Flush(List<DocumentSection> sections, string heading, StringBuilder body)
    {
        string text = body.ToString().Trim();
        if (text.Length > 0 || heading.Length > 0)
        {
            sections.Add(new DocumentSection(heading, text));
        }

        body.Clear();
    }
}

/// <summary>
/// OpenAI 兼容 embedding(当前:本地 Ollama bge-m3)。
/// 维度契约钉死 1024(七审 #3):向量列是 vector(1024),更换维度=schema 迁移+语料重建,
/// 不是配置项;每条返回向量都校验维度。
/// </summary>
public class OpenAiCompatibleEmbeddingService : IEmbeddingService
{
    public const int FixedDimensions = 1024;

    private readonly IEmbeddingGenerator<string, Embedding<float>> _generator;

    public OpenAiCompatibleEmbeddingService(IConfiguration configuration)
    {
        string baseUrl = configuration["Llm:EmbeddingBaseUrl"] ?? "http://localhost:11434/v1";
        string model = configuration["Llm:EmbeddingModel"] ?? "bge-m3";
        string apiKey = configuration["Llm:EmbeddingApiKey"] ?? "ollama";

        _generator = new OpenAIClient(
                new ApiKeyCredential(apiKey),
                new OpenAIClientOptions { Endpoint = new Uri(baseUrl) })
            .GetEmbeddingClient(model)
            .AsIEmbeddingGenerator();
    }

    public int Dimensions => FixedDimensions;

    public async Task<IReadOnlyList<float[]>> EmbedAsync(
        IReadOnlyList<string> texts, CancellationToken cancellationToken)
    {
        var result = await _generator.GenerateAsync(texts, cancellationToken: cancellationToken);
        var vectors = result.Select(embedding => embedding.Vector.ToArray()).ToList();
        foreach (float[] vector in vectors)
        {
            if (vector.Length != FixedDimensions)
            {
                throw new InvalidOperationException(
                    $"Embedding 服务返回 {vector.Length} 维向量,列契约为 vector({FixedDimensions})。");
            }
        }

        return vectors;
    }
}

/// <summary>原子替换写入(七审 #1):删旧 + 插新 + 写向量在同一事务,失败整体回滚。</summary>
public class KnowledgeWriter : IKnowledgeWriter
{
    private readonly ApplicationDbContext _context;

    public KnowledgeWriter(ApplicationDbContext context) => _context = context;

    public async Task<int> ReplaceDocumentAsync(
        string sourceFileName,
        KnowledgeDocument document,
        IReadOnlyList<float[]> chunkEmbeddings,
        CancellationToken cancellationToken)
    {
        var chunks = document.Chunks.OrderBy(chunk => chunk.Sequence).ToList();
        if (chunks.Count != chunkEmbeddings.Count)
        {
            throw new InvalidOperationException(
                $"分块 {chunks.Count} 条与向量 {chunkEmbeddings.Count} 条不匹配。");
        }

        var strategy = _context.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);

            KnowledgeDocument? existing = await _context.KnowledgeDocuments
                .FirstOrDefaultAsync(item => item.SourceFileName == sourceFileName, cancellationToken);
            if (existing is not null)
            {
                _context.KnowledgeDocuments.Remove(existing);
            }

            _context.KnowledgeDocuments.Add(document);
            for (int index = 0; index < chunks.Count; index++)
            {
                _context.Entry(chunks[index]).Property("Embedding").CurrentValue =
                    new Vector(chunkEmbeddings[index]);
            }

            await _context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return document.Id;
        });
    }
}

/// <summary>
/// pgvector 余弦检索(原生 SQL,显式租户条件)。
/// 相关性阈值(七审 #6):低于 Knowledge:MinSimilarity(默认 0.45)的分块拒答,
/// "知识库没有结果"在非空库里成为可达状态。
/// </summary>
public class KnowledgeSearch : IKnowledgeSearch
{
    private readonly ApplicationDbContext _context;
    private readonly ITenantContext _tenantContext;
    private readonly double _minSimilarity;

    public KnowledgeSearch(ApplicationDbContext context, ITenantContext tenantContext, IConfiguration configuration)
    {
        _context = context;
        _tenantContext = tenantContext;
        _minSimilarity = double.TryParse(configuration["Knowledge:MinSimilarity"], out double value) ? value : 0.45;
    }

    public async Task<IReadOnlyList<KnowledgeHit>> SearchAsync(
        float[] queryEmbedding, int topK, CancellationToken cancellationToken)
    {
        string tenant = _tenantContext.TenantId;
        var vector = new Vector(queryEmbedding);
        List<HitRow> rows = await _context.Database.SqlQuery<HitRow>($"""
            SELECT c."Id" AS "ChunkId", d."Title" AS "DocumentTitle", c."Section", c."Text",
                   1 - (c."Embedding" <=> {vector}) AS "Similarity"
            FROM "KnowledgeChunks" c
            JOIN "KnowledgeDocuments" d ON d."TenantId" = c."TenantId" AND d."Id" = c."DocumentId"
            WHERE c."TenantId" = {tenant}
              AND c."Embedding" IS NOT NULL
              AND 1 - (c."Embedding" <=> {vector}) >= {_minSimilarity}
            ORDER BY c."Embedding" <=> {vector}
            LIMIT {topK}
            """).ToListAsync(cancellationToken);
        return rows.Select(row => new KnowledgeHit(row.ChunkId, row.DocumentTitle, row.Section, row.Text, row.Similarity)).ToList();
    }

    private sealed class HitRow
    {
        public int ChunkId { get; set; }

        public string DocumentTitle { get; set; } = string.Empty;

        public string Section { get; set; } = string.Empty;

        public string Text { get; set; } = string.Empty;

        public double Similarity { get; set; }
    }
}
