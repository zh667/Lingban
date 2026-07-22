namespace Lingban.Domain.Entities.Materials;

/// <summary>
/// 工位实记的物料消耗——谱系的边:来料批次 →(本记录)→ 工单 → 产出批次。
/// 只能通过 WorkOrder.RecordConsumption 创建,保证账实一致。
/// </summary>
public class MaterialConsumption : BaseAuditableEntity, ITenantEntity
{
    public string TenantId { get; set; } = string.Empty;

    public int WorkOrderId { get; internal set; }

    public Production.WorkOrder WorkOrder { get; set; } = null!;

    public int MaterialLotId { get; internal set; }

    public MaterialLot MaterialLot { get; set; } = null!;

    public decimal Quantity { get; internal set; }

    public string UnitOfMeasure { get; internal set; } = "PCS";

    public int WorkstationId { get; internal set; }

    public Production.Workstation Workstation { get; set; } = null!;

    public DateTimeOffset RecordedAtUtc { get; internal set; }

    public string? RecordedBy { get; internal set; }

    internal MaterialConsumption() { }
}
