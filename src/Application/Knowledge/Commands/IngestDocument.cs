using Lingban.Application.Common.Interfaces;
using Lingban.Domain.Entities.Knowledge;

namespace Lingban.Application.Knowledge.Commands;

/// <summary>
/// 文档入库(七审 #1 原子语义):解析 → 分块 → 批量向量化(计数与维度 fail-fast)
/// 全部成功后,才在**单个数据库事务**里替换旧版(删旧 + 插新 + 写向量)。
/// 任何一步失败,旧版原样保留、可继续检索。
/// </summary>
public record IngestDocumentCommand : IRequest<int>
{
    public string FileName { get; init; } = string.Empty;

    public byte[] Content { get; init; } = Array.Empty<byte>();
}

public class IngestDocumentCommandValidator : AbstractValidator<IngestDocumentCommand>
{
    public IngestDocumentCommandValidator()
    {
        RuleFor(command => command.FileName).NotEmpty().MaximumLength(256)
            .Must(name => name.EndsWith(".docx", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
            .WithMessage("仅支持 .docx / .md / .txt");
        RuleFor(command => command.Content).NotEmpty()
            .Must(content => content.Length <= 5 * 1024 * 1024).WithMessage("文档不得超过 5MB");
    }
}

public record DocumentSection(string Section, string Text);

/// <summary>按扩展名解析文档为章节序列(docx 含表格与标题层级路径;md/txt 按标题行)。</summary>
public interface IDocumentParser
{
    IReadOnlyList<DocumentSection> Parse(string fileName, byte[] content);
}

public class IngestDocumentCommandHandler : IRequestHandler<IngestDocumentCommand, int>
{
    private const int MaxChunkChars = 800;
    private const int EmbeddingBatchSize = 64;

    private readonly IDocumentParser _parser;
    private readonly IEmbeddingService _embeddings;
    private readonly IKnowledgeWriter _writer;

    public IngestDocumentCommandHandler(
        IDocumentParser parser,
        IEmbeddingService embeddings,
        IKnowledgeWriter writer)
    {
        _parser = parser;
        _embeddings = embeddings;
        _writer = writer;
    }

    public async Task<int> Handle(IngestDocumentCommand request, CancellationToken cancellationToken)
    {
        IReadOnlyList<DocumentSection> sections = _parser.Parse(request.FileName, request.Content);
        if (sections.Count == 0)
        {
            throw new InvalidOperationException($"文档 {request.FileName} 没有可入库的内容。");
        }

        var document = new KnowledgeDocument
        {
            Title = sections[0].Section.Length > 0 ? sections[0].Section.Split('>')[0].Trim() : request.FileName,
            SourceFileName = request.FileName
        };

        var chunks = new List<KnowledgeChunk>();
        int sequence = 0;
        foreach (DocumentSection section in sections)
        {
            foreach (string piece in SplitByParagraphs(section.Text))
            {
                var chunk = new KnowledgeChunk
                {
                    Document = document,
                    Sequence = sequence++,
                    Section = section.Section,
                    Text = piece
                };
                document.Chunks.Add(chunk);
                chunks.Add(chunk);
            }
        }

        // 先算全部向量(分批;计数与维度 fail-fast),数据库在此之前零改动。
        var vectors = new List<float[]>(chunks.Count);
        for (int offset = 0; offset < chunks.Count; offset += EmbeddingBatchSize)
        {
            List<string> batch = chunks.Skip(offset).Take(EmbeddingBatchSize)
                .Select(chunk => $"{chunk.Section}\n{chunk.Text}").ToList();
            IReadOnlyList<float[]> batchVectors = await _embeddings.EmbedAsync(batch, cancellationToken);
            if (batchVectors.Count != batch.Count)
            {
                throw new InvalidOperationException(
                    $"Embedding 服务返回 {batchVectors.Count} 条向量,期望 {batch.Count} 条。");
            }

            foreach (float[] vector in batchVectors)
            {
                if (vector.Length != _embeddings.Dimensions)
                {
                    throw new InvalidOperationException(
                        $"Embedding 维度 {vector.Length} 与契约 {_embeddings.Dimensions} 不符;" +
                        "更换维度需要 schema 迁移与语料重建,不能只改配置。");
                }

                vectors.Add(vector);
            }
        }

        // 单事务替换:删旧 + 插新 + 写向量,失败整体回滚。
        return await _writer.ReplaceDocumentAsync(request.FileName, document, vectors, cancellationToken);
    }

    /// <summary>按段落边界聚合切块(七审 #9):不拦腰截断;单段超长时按句子再退化硬切。</summary>
    private static IEnumerable<string> SplitByParagraphs(string text)
    {
        var buffer = new System.Text.StringBuilder();
        foreach (string paragraph in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (buffer.Length > 0 && buffer.Length + paragraph.Length + 1 > MaxChunkChars)
            {
                yield return buffer.ToString();
                buffer.Clear();
            }

            if (paragraph.Length <= MaxChunkChars)
            {
                if (buffer.Length > 0)
                {
                    buffer.Append('\n');
                }

                buffer.Append(paragraph);
                continue;
            }

            if (buffer.Length > 0)
            {
                yield return buffer.ToString();
                buffer.Clear();
            }

            foreach (string piece in SplitLongParagraph(paragraph))
            {
                yield return piece;
            }
        }

        if (buffer.Length > 0)
        {
            yield return buffer.ToString();
        }
    }

    private static IEnumerable<string> SplitLongParagraph(string paragraph)
    {
        var buffer = new System.Text.StringBuilder();
        foreach (string sentence in paragraph.Split('。', StringSplitOptions.RemoveEmptyEntries))
        {
            string unit = sentence + "。";
            if (buffer.Length > 0 && buffer.Length + unit.Length > MaxChunkChars)
            {
                yield return buffer.ToString();
                buffer.Clear();
            }

            if (unit.Length <= MaxChunkChars)
            {
                buffer.Append(unit);
                continue;
            }

            for (int index = 0; index < unit.Length; index += MaxChunkChars)
            {
                yield return unit.Substring(index, Math.Min(MaxChunkChars, unit.Length - index));
            }
        }

        if (buffer.Length > 0)
        {
            yield return buffer.ToString();
        }
    }
}
