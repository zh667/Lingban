using Lingban.Application.Common.Interfaces;
using Lingban.Domain.Entities.Knowledge;

namespace Lingban.Application.Knowledge.Commands;

/// <summary>
/// 文档入库:解析(基础设施层按扩展名选解析器)→ 分块 → 向量化 → 落库。
/// 同名文件重新入库 = 全量替换(SOP 有版本语义,旧块必须消失)。
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

/// <summary>按扩展名解析文档为章节序列(docx 用 OpenXml,md/txt 按标题行)。</summary>
public interface IDocumentParser
{
    IReadOnlyList<DocumentSection> Parse(string fileName, byte[] content);
}

public class IngestDocumentCommandHandler : IRequestHandler<IngestDocumentCommand, int>
{
    private const int MaxChunkChars = 800;

    private readonly IApplicationDbContext _context;
    private readonly IDocumentParser _parser;
    private readonly IEmbeddingService _embeddings;
    private readonly IKnowledgeChunkWriter _chunkWriter;

    public IngestDocumentCommandHandler(
        IApplicationDbContext context,
        IDocumentParser parser,
        IEmbeddingService embeddings,
        IKnowledgeChunkWriter chunkWriter)
    {
        _context = context;
        _parser = parser;
        _embeddings = embeddings;
        _chunkWriter = chunkWriter;
    }

    public async Task<int> Handle(IngestDocumentCommand request, CancellationToken cancellationToken)
    {
        IReadOnlyList<DocumentSection> sections = _parser.Parse(request.FileName, request.Content);
        if (sections.Count == 0)
        {
            throw new InvalidOperationException($"文档 {request.FileName} 没有可入库的内容。");
        }

        // 同名替换:旧文档与旧块删除(旧 SOP 不允许残留在检索里)。
        KnowledgeDocument? existing = await _context.KnowledgeDocuments
            .FirstOrDefaultAsync(document => document.SourceFileName == request.FileName, cancellationToken);
        if (existing is not null)
        {
            _context.KnowledgeDocuments.Remove(existing);
            await _context.SaveChangesAsync(cancellationToken);
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
            foreach (string piece in Split(section.Text))
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

        IReadOnlyList<float[]> vectors = await _embeddings.EmbedAsync(
            chunks.Select(chunk => $"{chunk.Section}\n{chunk.Text}").ToList(), cancellationToken);

        _context.KnowledgeDocuments.Add(document);
        await _context.SaveChangesAsync(cancellationToken);
        await _chunkWriter.WriteEmbeddingsAsync(
            chunks.Select((chunk, index) => (chunk.Id, vectors[index])).ToList(), cancellationToken);

        return document.Id;
    }

    private static IEnumerable<string> Split(string text)
    {
        text = text.Trim();
        if (text.Length <= MaxChunkChars)
        {
            if (text.Length > 0)
            {
                yield return text;
            }

            yield break;
        }

        for (int index = 0; index < text.Length; index += MaxChunkChars)
        {
            yield return text.Substring(index, Math.Min(MaxChunkChars, text.Length - index));
        }
    }
}
