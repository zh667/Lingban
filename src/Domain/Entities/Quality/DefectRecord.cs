namespace Lingban.Domain.Entities.Quality;

public class DefectRecord : BaseAuditableEntity, ITenantEntity
{
    public string TenantId { get; set; } = string.Empty;

    public int? WorkOrderId { get; set; }

    public Production.WorkOrder? WorkOrder { get; set; }

    public int DefectTypeId { get; set; }

    public DefectType DefectType { get; set; } = null!;

    public decimal Quantity { get; set; }

    public string? Notes { get; set; }

    public DateTimeOffset RecordedAtUtc { get; set; }
}
