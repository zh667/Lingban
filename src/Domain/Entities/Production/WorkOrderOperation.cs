namespace Lingban.Domain.Entities.Production;

/// <summary>工单工序实例(由工艺路线展开)。</summary>
public class WorkOrderOperation : BaseAuditableEntity, ITenantEntity
{
    public string TenantId { get; set; } = string.Empty;

    public int WorkOrderId { get; set; }

    public WorkOrder WorkOrder { get; set; } = null!;

    public int Sequence { get; set; }

    public string Name { get; set; } = string.Empty;

    public int? WorkstationId { get; set; }

    public Workstation? Workstation { get; set; }
}
