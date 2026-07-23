namespace Lingban.Application.Common.Interfaces;

public record KnowledgeHit(int ChunkId, string DocumentTitle, string Section, string Text, double Similarity);

/// <summary>向量检索(基础设施层 pgvector 原生 SQL 实现)。</summary>
public interface IKnowledgeSearch
{
    Task<IReadOnlyList<KnowledgeHit>> SearchAsync(float[] queryEmbedding, int topK, CancellationToken cancellationToken);
}
