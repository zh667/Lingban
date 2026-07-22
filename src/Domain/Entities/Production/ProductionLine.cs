namespace Lingban.Domain.Entities.Production;

public class ProductionLine : BaseAuditableEntity, ITenantEntity
{
    public string TenantId { get; set; } = string.Empty;

    public string Code { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public ICollection<Workstation> Workstations { get; private set; } = new List<Workstation>();
}
