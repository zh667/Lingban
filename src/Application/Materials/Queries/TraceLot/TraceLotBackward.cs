using Lingban.Application.Common.Interfaces;
using Lingban.Domain.Entities.Materials;

namespace Lingban.Application.Materials.Queries.TraceLot;

/// <summary>
/// 反向追溯:给定(成品)批次,列出构成它的全部来料批次——客诉时回答"这台机器用了哪些料"。
/// </summary>
public record TraceLotBackwardQuery(int LotId) : IRequest<LotTraceNode>;

public class TraceLotBackwardQueryHandler : IRequestHandler<TraceLotBackwardQuery, LotTraceNode>
{
    private readonly IApplicationDbContext _context;

    public TraceLotBackwardQueryHandler(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<LotTraceNode> Handle(TraceLotBackwardQuery request, CancellationToken cancellationToken)
    {
        MaterialLot? root = await _context.MaterialLots
            .AsNoTracking()
            .Include(lot => lot.Product)
            .FirstOrDefaultAsync(lot => lot.Id == request.LotId, cancellationToken);

        Guard.Against.NotFound(request.LotId, root);

        var visited = new HashSet<int> { root.Id };
        return await BuildBackwardNodeAsync(root, viaWorkOrder: null, visited, cancellationToken);
    }

    private async Task<LotTraceNode> BuildBackwardNodeAsync(
        MaterialLot lot,
        (int Id, string Code)? viaWorkOrder,
        HashSet<int> visited,
        CancellationToken cancellationToken)
    {
        var children = new List<LotTraceNode>();

        // 采购批次(无产出工单)是谱系叶子;自产批次沿产出工单的消耗记录上溯。
        if (lot.ProducedByWorkOrderId is int workOrderId)
        {
            string workOrderCode = await _context.WorkOrders
                .AsNoTracking()
                .Where(order => order.Id == workOrderId)
                .Select(order => order.Code)
                .FirstAsync(cancellationToken);

            List<MaterialLot> sources = await _context.MaterialLots
                .AsNoTracking()
                .Include(source => source.Product)
                .Where(source => source.Consumptions.Any(
                    consumption => consumption.WorkOrderId == workOrderId))
                .ToListAsync(cancellationToken);

            foreach (MaterialLot source in sources)
            {
                if (!visited.Add(source.Id))
                {
                    continue;
                }

                children.Add(await BuildBackwardNodeAsync(
                    source, (workOrderId, workOrderCode), visited, cancellationToken));
            }
        }

        return new LotTraceNode
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
}
