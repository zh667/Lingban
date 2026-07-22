namespace Lingban.Domain.Entities.Materials;

/// <summary>
/// 物料批次。谱系的节点:采购批次 SupplierLotNumber 溯源到供应商,
/// 自产批次通过 ProducedByWorkOrderId 挂到产出它的工单。
/// </summary>
public class MaterialLot : BaseAuditableEntity, ITenantEntity
{
    public string TenantId { get; set; } = string.Empty;

    public string LotNumber { get; set; } = string.Empty;

    public int ProductId { get; set; }

    public Product Product { get; set; } = null!;

    public decimal InitialQuantity { get; private set; }

    public decimal QuantityOnHand { get; private set; }

    public string UnitOfMeasure { get; set; } = "PCS";

    public LotStatus Status { get; private set; } = LotStatus.Available;

    public DateTimeOffset ReceivedAtUtc { get; set; }

    /// <summary>采购批次的供应商批号;自产批次为 null。</summary>
    public string? SupplierLotNumber { get; set; }

    /// <summary>产出本批次的工单;采购批次为 null。谱系的"向上"边,只能由 WorkOrder.ProduceLot 建立。</summary>
    public int? ProducedByWorkOrderId { get; private set; }

    public Production.WorkOrder? ProducedByWorkOrder { get; internal set; }

    /// <summary>本批次被消耗的记录。谱系的"向下"边。</summary>
    public ICollection<MaterialConsumption> Consumptions { get; private set; } = new List<MaterialConsumption>();

    public static MaterialLot Create(
        string lotNumber,
        int productId,
        decimal quantity,
        string unitOfMeasure,
        DateTimeOffset receivedAtUtc,
        string? supplierLotNumber = null)
    {
        if (string.IsNullOrWhiteSpace(lotNumber))
        {
            throw new ArgumentException("Lot number is required.", nameof(lotNumber));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(quantity);

        return new MaterialLot
        {
            LotNumber = lotNumber,
            ProductId = productId,
            InitialQuantity = quantity,
            QuantityOnHand = quantity,
            UnitOfMeasure = unitOfMeasure,
            ReceivedAtUtc = receivedAtUtc.ToUniversalTime(),
            SupplierLotNumber = supplierLotNumber
        };
    }

    /// <summary>从批次扣减库存。由 WorkOrder.RecordConsumption 调用,不直接暴露给应用层。</summary>
    internal void Consume(decimal quantity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(quantity);

        if (Status != LotStatus.Available)
        {
            throw new InvalidOperationException($"Lot {LotNumber} is {Status} and cannot be consumed.");
        }

        if (quantity > QuantityOnHand)
        {
            throw new InvalidOperationException(
                $"Lot {LotNumber} has {QuantityOnHand} on hand; cannot consume {quantity}.");
        }

        QuantityOnHand -= quantity;
    }

    public void Block() => Status = LotStatus.Blocked;

    public void Unblock() => Status = LotStatus.Available;
}
