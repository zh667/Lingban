using Lingban.Application.Common.Interfaces;
using Lingban.Domain.Entities.Materials;

namespace Lingban.Application.Materials.Queries.TraceLot;

/// <summary>
/// 正向追溯:给定(来料)批次,列出它经由哪些工单流入了哪些下游批次——召回时回答"这批坏料流到了哪里"。
/// </summary>
public record TraceLotForwardQuery(int LotId) : IRequest<LotTraceNode>;

public class TraceLotForwardQueryHandler : IRequestHandler<TraceLotForwardQuery, LotTraceNode>
{
    private readonly IApplicationDbContext _context;

    public TraceLotForwardQueryHandler(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<LotTraceNode> Handle(TraceLotForwardQuery request, CancellationToken cancellationToken)
    {
        MaterialLot? root = await _context.MaterialLots
            .AsNoTracking()
            .Include(lot => lot.Product)
            .FirstOrDefaultAsync(lot => lot.Id == request.LotId, cancellationToken);

        Guard.Against.NotFound(request.LotId, root);

        // 访问集是"当前递归路径"(防环),不是全树共享集合——
        // 菱形谱系(同一批次流经多条支路)必须在每条支路都如实出现。
        var path = new HashSet<int>();
        return await BuildForwardNodeAsync(root, viaWorkOrder: null, path, cancellationToken);
    }

    private async Task<LotTraceNode> BuildForwardNodeAsync(
        MaterialLot lot,
        (int Id, string Code)? viaWorkOrder,
        HashSet<int> path,
        CancellationToken cancellationToken)
    {
        // 本批次被哪些工单消耗。
        var consumingOrders = await _context.MaterialConsumptions
            .AsNoTracking()
            .Where(consumption => consumption.MaterialLotId == lot.Id)
            .Select(consumption => new { consumption.WorkOrderId, consumption.WorkOrder.Code })
            .Distinct()
            .ToListAsync(cancellationToken);

        var children = new List<LotTraceNode>();
        path.Add(lot.Id);
        foreach (var order in consumingOrders)
        {
            // 这些工单产出的批次,即下游节点。
            List<MaterialLot> outputs = await _context.MaterialLots
                .AsNoTracking()
                .Include(output => output.Product)
                .Where(output => output.ProducedByWorkOrderId == order.WorkOrderId)
                .ToListAsync(cancellationToken);

            foreach (MaterialLot output in outputs)
            {
                if (path.Contains(output.Id))
                {
                    continue;
                }

                children.Add(await BuildForwardNodeAsync(
                    output, (order.WorkOrderId, order.Code), path, cancellationToken));
            }
        }

        path.Remove(lot.Id);
        return ToNode(lot, viaWorkOrder, children);
    }

    private static LotTraceNode ToNode(
        MaterialLot lot,
        (int Id, string Code)? viaWorkOrder,
        IReadOnlyList<LotTraceNode> children) => new()
        {
            LotId = lot.Id,
            LotNumber = lot.LotNumber,
            ProductCode = lot.Product.Code,
            ProductName = lot.Product.Name,
            SupplierLotNumber = lot.SupplierLotNumber,
            ViaWorkOrderId = viaWorkOrder?.Id,
            ViaWorkOrderCode = viaWorkOrder?.Code,
            Children = children
        };
}
