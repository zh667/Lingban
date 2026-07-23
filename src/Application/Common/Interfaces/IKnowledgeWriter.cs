using Lingban.Domain.Entities.Knowledge;

namespace Lingban.Application.Common.Interfaces;

/// <summary>
/// 知识文档原子替换:删旧版 + 插新版 + 写向量在同一数据库事务内完成,
/// 失败整体回滚,旧版保持可检索(七审 #1)。
/// </summary>
public interface IKnowledgeWriter
{
    Task<int> ReplaceDocumentAsync(
        string sourceFileName,
        KnowledgeDocument document,
        IReadOnlyList<float[]> chunkEmbeddings,
        CancellationToken cancellationToken);
}
