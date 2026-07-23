using System.ClientModel;
using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Lingban.Application.Common.Interfaces;
using Lingban.Application.Knowledge.Commands;
using Lingban.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using OpenAI;
using Pgvector;

namespace Lingban.Infrastructure.Knowledge;

/// <summary>docx(OpenXml,按标题样式分节)与 md/txt(按 # / 数字标题行分节)。</summary>
public class DocumentParser : IDocumentParser
{
    public IReadOnlyList<DocumentSection> Parse(string fileName, byte[] content)
    {
        return fileName.EndsWith(".docx", StringComparison.OrdinalIgnoreCase)
            ? ParseDocx(content)
            : ParseText(Encoding.UTF8.GetString(content));
    }

    private static IReadOnlyList<DocumentSection> ParseDocx(byte[] content)
    {
        using var stream = new MemoryStream(content);
        using WordprocessingDocument word = WordprocessingDocument.Open(stream, false);
        Body body = word.MainDocumentPart?.Document?.Body
            ?? throw new InvalidOperationException("docx 缺少文档主体。");
        var sections = new List<DocumentSection>();
        var current = new StringBuilder();
        string heading = string.Empty;

        foreach (Paragraph paragraph in body.Elements<Paragraph>())
        {
            string text = paragraph.InnerText.Trim();
            if (text.Length == 0)
            {
                continue;
            }

            string? style = paragraph.ParagraphProperties?.ParagraphStyleId?.Val?.Value;
            bool isHeading = style?.StartsWith("Heading", StringComparison.OrdinalIgnoreCase) == true
                || style?.StartsWith("berschrift", StringComparison.OrdinalIgnoreCase) == true
                || style == "1" || style == "2" || style == "3";
            if (isHeading)
            {
                Flush(sections, heading, current);
                heading = text;
            }
            else
            {
                current.AppendLine(text);
            }
        }

        Flush(sections, heading, current);
        return sections;
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

/// <summary>OpenAI 兼容 embedding(当前指向本地 Ollama bge-m3;Llm:Embedding* 可配置替换)。</summary>
public class OpenAiCompatibleEmbeddingService : IEmbeddingService
{
    private readonly IEmbeddingGenerator<string, Embedding<float>> _generator;

    public OpenAiCompatibleEmbeddingService(IConfiguration configuration)
    {
        string baseUrl = configuration["Llm:EmbeddingBaseUrl"] ?? "http://localhost:11434/v1";
        string model = configuration["Llm:EmbeddingModel"] ?? "bge-m3";
        string apiKey = configuration["Llm:EmbeddingApiKey"] ?? "ollama";
        Dimensions = int.TryParse(configuration["Llm:EmbeddingDimensions"], out int dims) ? dims : 1024;

        _generator = new OpenAIClient(
                new ApiKeyCredential(apiKey),
                new OpenAIClientOptions { Endpoint = new Uri(baseUrl) })
            .GetEmbeddingClient(model)
            .AsIEmbeddingGenerator();
    }

    public int Dimensions { get; }

    public async Task<IReadOnlyList<float[]>> EmbedAsync(
        IReadOnlyList<string> texts, CancellationToken cancellationToken)
    {
        var result = await _generator.GenerateAsync(texts, cancellationToken: cancellationToken);
        return result.Select(embedding => embedding.Vector.ToArray()).ToList();
    }
}

/// <summary>向量写入:shadow 属性,应用层不接触 pgvector 类型。</summary>
public class KnowledgeChunkWriter : IKnowledgeChunkWriter
{
    private readonly ApplicationDbContext _context;

    public KnowledgeChunkWriter(ApplicationDbContext context) => _context = context;

    public async Task WriteEmbeddingsAsync(
        IReadOnlyList<(int ChunkId, float[] Embedding)> embeddings, CancellationToken cancellationToken)
    {
        foreach ((int chunkId, float[] embedding) in embeddings)
        {
            var chunk = await _context.KnowledgeChunks.FirstAsync(c => c.Id == chunkId, cancellationToken);
            _context.Entry(chunk).Property("Embedding").CurrentValue = new Vector(embedding);
        }

        await _context.SaveChangesAsync(cancellationToken);
    }
}

/// <summary>pgvector 余弦检索(原生 SQL,显式租户条件)。</summary>
public class KnowledgeSearch : IKnowledgeSearch
{
    private readonly ApplicationDbContext _context;
    private readonly ITenantContext _tenantContext;

    public KnowledgeSearch(ApplicationDbContext context, ITenantContext tenantContext)
    {
        _context = context;
        _tenantContext = tenantContext;
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
            WHERE c."TenantId" = {tenant} AND c."Embedding" IS NOT NULL
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
