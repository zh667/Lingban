namespace Lingban.Domain.Entities.Knowledge;

public class KnowledgeDocument : BaseAuditableEntity, ITenantEntity
{
    public string TenantId { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string SourceFileName { get; set; } = string.Empty;

    public ICollection<KnowledgeChunk> Chunks { get; private set; } = new List<KnowledgeChunk>();
}
