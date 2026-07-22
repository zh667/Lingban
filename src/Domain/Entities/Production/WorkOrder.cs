using Lingban.Domain.Entities.Materials;

namespace Lingban.Domain.Entities.Production;

/// <summary>
/// 生产工单聚合根。
/// 领域铁律:状态只能通过转换方法改变(非法转换抛异常);
/// 数量分完工/合格/报废/返工四账,超产不截断;
/// 物料消耗只能经 RecordConsumption 记账,保证批次谱系真实。
/// </summary>
public class WorkOrder : BaseAuditableEntity, ITenantEntity
{
    private readonly List<MaterialConsumption> _consumptions = new();

    public string TenantId { get; set; } = string.Empty;

    public string Code { get; private set; } = string.Empty;

    public int ProductId { get; private set; }

    public Materials.Product Product { get; set; } = null!;

    public int ProductionLineId { get; private set; }

    public ProductionLine ProductionLine { get; set; } = null!;

    public decimal PlannedQuantity { get; private set; }

    public string UnitOfMeasure { get; private set; } = "PCS";

    public decimal CompletedQuantity { get; private set; }

    public decimal QualifiedQuantity { get; private set; }

    public decimal ScrapQuantity { get; private set; }

    public decimal ReworkQuantity { get; private set; }

    public WorkOrderStatus Status { get; private set; } = WorkOrderStatus.Draft;

    public DateTimeOffset? PlannedStartUtc { get; set; }

    public DateTimeOffset? PlannedEndUtc { get; set; }

    public DateTimeOffset? ActualStartUtc { get; private set; }

    public DateTimeOffset? ActualEndUtc { get; private set; }

    /// <summary>超产是需要暴露的信号,不做 Math.Min 截断。</summary>
    public bool IsOverproduced => CompletedQuantity > PlannedQuantity;

    public ICollection<WorkOrderOperation> Operations { get; private set; } = new List<WorkOrderOperation>();

    public IReadOnlyCollection<MaterialConsumption> Consumptions => _consumptions.AsReadOnly();

    /// <summary>本工单产出的批次(MaterialLot.ProducedByWorkOrderId 反向导航)。</summary>
    public ICollection<MaterialLot> OutputLots { get; private set; } = new List<MaterialLot>();

    private WorkOrder() { }

    public static WorkOrder Create(
        string code,
        int productId,
        int productionLineId,
        decimal plannedQuantity,
        string unitOfMeasure)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new ArgumentException("Work order code is required.", nameof(code));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(plannedQuantity);

        return new WorkOrder
        {
            Code = code,
            ProductId = productId,
            ProductionLineId = productionLineId,
            PlannedQuantity = plannedQuantity,
            UnitOfMeasure = unitOfMeasure
        };
    }

    public void Release() => Transition(WorkOrderStatus.Released, from: WorkOrderStatus.Draft);

    public void Start(DateTimeOffset occurredAtUtc)
    {
        Transition(WorkOrderStatus.InProgress, from: WorkOrderStatus.Released);
        ActualStartUtc = occurredAtUtc.ToUniversalTime();
    }

    public void Complete(DateTimeOffset occurredAtUtc)
    {
        Transition(WorkOrderStatus.Completed, from: WorkOrderStatus.InProgress);
        ActualEndUtc = occurredAtUtc.ToUniversalTime();
    }

    public void Cancel()
    {
        if (Status is not (WorkOrderStatus.Draft or WorkOrderStatus.Released))
        {
            throw new InvalidWorkOrderTransitionException(Status, WorkOrderStatus.Cancelled);
        }

        Status = WorkOrderStatus.Cancelled;
    }

    /// <summary>
    /// 报工:四账分记,只校验非负,不截断超产。
    /// </summary>
    public void ReportProduction(decimal completed, decimal qualified, decimal scrap, decimal rework)
    {
        EnsureInProgress("report production");
        ArgumentOutOfRangeException.ThrowIfNegative(completed);
        ArgumentOutOfRangeException.ThrowIfNegative(qualified);
        ArgumentOutOfRangeException.ThrowIfNegative(scrap);
        ArgumentOutOfRangeException.ThrowIfNegative(rework);

        CompletedQuantity += completed;
        QualifiedQuantity += qualified;
        ScrapQuantity += scrap;
        ReworkQuantity += rework;

        // 四账不变量:合格+报废+返工是完工的处置分账,不得超过完工总量。
        if (QualifiedQuantity + ScrapQuantity + ReworkQuantity > CompletedQuantity)
        {
            CompletedQuantity -= completed;
            QualifiedQuantity -= qualified;
            ScrapQuantity -= scrap;
            ReworkQuantity -= rework;
            throw new InvalidOperationException(
                $"Work order {Code}: qualified + scrap + rework would exceed completed quantity.");
        }
    }

    /// <summary>
    /// 工位实记消耗:扣减批次库存并生成谱系边。这是批次追溯真实性的唯一入口。
    /// </summary>
    public MaterialConsumption RecordConsumption(
        MaterialLot lot,
        decimal quantity,
        int workstationId,
        DateTimeOffset recordedAtUtc,
        string? recordedBy = null,
        Guid? eventId = null)
    {
        EnsureInProgress("record material consumption");

        // 禁止自耗环:工单不能消耗自己产出的批次。
        if (ReferenceEquals(lot.ProducedByWorkOrder, this) ||
            (lot.ProducedByWorkOrderId is int producerId && Id != 0 && producerId == Id))
        {
            throw new InvalidOperationException(
                $"Work order {Code} cannot consume its own output lot {lot.LotNumber}.");
        }

        // 租户一致性(两边都已盖章时校验;未盖章的由基础设施层保证)。
        if (!string.IsNullOrEmpty(TenantId) && !string.IsNullOrEmpty(lot.TenantId) && TenantId != lot.TenantId)
        {
            throw new InvalidOperationException(
                $"Work order {Code} and lot {lot.LotNumber} belong to different tenants.");
        }

        lot.Consume(quantity);

        var consumption = new MaterialConsumption
        {
            WorkOrder = this,
            WorkOrderId = Id,
            MaterialLot = lot,
            MaterialLotId = lot.Id,
            Quantity = quantity,
            UnitOfMeasure = lot.UnitOfMeasure,
            WorkstationId = workstationId,
            RecordedAtUtc = recordedAtUtc.ToUniversalTime(),
            RecordedBy = recordedBy,
            EventId = eventId
        };

        _consumptions.Add(consumption);
        return consumption;
    }

    /// <summary>产出批次入库,自动挂谱系(ProducedByWorkOrder)。</summary>
    public MaterialLot ProduceLot(string lotNumber, decimal quantity, DateTimeOffset producedAtUtc)
    {
        EnsureInProgress("produce an output lot");

        var lot = MaterialLot.Create(
            lotNumber,
            ProductId,
            quantity,
            UnitOfMeasure,
            producedAtUtc,
            supplierLotNumber: null);
        lot.ProducedByWorkOrder = this;
        OutputLots.Add(lot);
        return lot;
    }

    private void Transition(WorkOrderStatus to, WorkOrderStatus from)
    {
        if (Status != from)
        {
            throw new InvalidWorkOrderTransitionException(Status, to);
        }

        Status = to;
    }

    private void EnsureInProgress(string action)
    {
        if (Status != WorkOrderStatus.InProgress)
        {
            throw new InvalidOperationException(
                $"Work order {Code} is {Status}; can only {action} while InProgress.");
        }
    }
}
