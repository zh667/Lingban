namespace Lingban.Domain.Entities.Production;

/// <summary>产品的工艺路线。</summary>
public class ProcessRoute : BaseAuditableEntity, ITenantEntity
{
    public string TenantId { get; set; } = string.Empty;

    public int ProductId { get; set; }

    public Materials.Product Product { get; set; } = null!;

    public string Name { get; set; } = string.Empty;

    public ICollection<ProcessStep> Steps { get; private set; } = new List<ProcessStep>();
}
