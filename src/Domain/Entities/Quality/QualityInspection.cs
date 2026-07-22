namespace Lingban.Domain.Entities.Quality;

public class QualityInspection : BaseAuditableEntity, ITenantEntity
{
    public string TenantId { get; set; } = string.Empty;

    public int? WorkOrderId { get; set; }

    public Production.WorkOrder? WorkOrder { get; set; }

    public int? MaterialLotId { get; set; }

    public Materials.MaterialLot? MaterialLot { get; set; }

    public InspectionResult Result { get; set; }

    public string? Notes { get; set; }

    public DateTimeOffset InspectedAtUtc { get; set; }
}
