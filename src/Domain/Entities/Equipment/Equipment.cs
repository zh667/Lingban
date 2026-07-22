namespace Lingban.Domain.Entities.Equipment;

public class Equipment : BaseAuditableEntity, ITenantEntity
{
    public string TenantId { get; set; } = string.Empty;

    public string Code { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string? Model { get; set; }

    public int? WorkstationId { get; set; }

    public Production.Workstation? Workstation { get; set; }

    /// <summary>理想节拍(秒/件),OEE 性能率的基准。</summary>
    public decimal IdealCycleTimeSeconds { get; set; }

    public bool IsActive { get; set; } = true;
}
