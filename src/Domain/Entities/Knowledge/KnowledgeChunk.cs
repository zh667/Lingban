namespace Lingban.Domain.Entities.Knowledge;

/// <summary>
/// 知识分块。向量列由基础设施层以 shadow 属性映射(Domain 不引用 pgvector 类型)。
/// </summary>
public class KnowledgeChunk : BaseAuditableEntity, ITenantEntity
{
    public string TenantId { get; set; } = string.Empty;

    public int DocumentId { get; set; }

    public KnowledgeDocument Document { get; set; } = null!;

    public int Sequence { get; set; }

    /// <summary>章节路径,如 "3 回流焊参数 > 3.2 温区设定"。引用契约的锚点。</summary>
    public string Section { get; set; } = string.Empty;

    public string Text { get; set; } = string.Empty;
}
