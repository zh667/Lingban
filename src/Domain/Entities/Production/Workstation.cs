namespace Lingban.Domain.Entities.Production;

public class Workstation : BaseAuditableEntity, ITenantEntity
{
    public string TenantId { get; set; } = string.Empty;

    public string Code { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public int ProductionLineId { get; set; }

    public ProductionLine ProductionLine { get; set; } = null!;
}
