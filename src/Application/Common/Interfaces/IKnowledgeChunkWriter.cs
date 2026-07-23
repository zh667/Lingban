namespace Lingban.Application.Common.Interfaces;

/// <summary>向量写入(shadow 属性由基础设施层持有,应用层只传 float[])。</summary>
public interface IKnowledgeChunkWriter
{
    Task WriteEmbeddingsAsync(IReadOnlyList<(int ChunkId, float[] Embedding)> embeddings, CancellationToken cancellationToken);
}
