namespace Lingban.Application.Common.Interfaces;

/// <summary>文本向量化。当前实现:本地 Ollama bge-m3(OpenAI 兼容端点),可配置替换。</summary>
public interface IEmbeddingService
{
    int Dimensions { get; }

    Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken);
}
