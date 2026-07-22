namespace Lingban.Application.Materials.Queries.TraceLot;

/// <summary>
/// 批次谱系树节点。Forward:Children 是本批次流入的下游批次;
/// Backward:Children 是构成本批次的上游来料批次。
/// ViaWorkOrder* 标注这条谱系边经过的工单。
/// </summary>
public record LotTraceNode
{
    public int LotId { get; init; }

    public string LotNumber { get; init; } = string.Empty;

    public string ProductCode { get; init; } = string.Empty;

    public string ProductName { get; init; } = string.Empty;

    public string? SupplierLotNumber { get; init; }

    public int? ViaWorkOrderId { get; init; }

    public string? ViaWorkOrderCode { get; init; }

    public IReadOnlyList<LotTraceNode> Children { get; init; } = Array.Empty<LotTraceNode>();
}
