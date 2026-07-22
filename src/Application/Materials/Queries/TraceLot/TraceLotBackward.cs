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

        // 访问集是"当前递归路径"(防环),不是全树共享集合——
        // 菱形谱系(同一批次流经多条支路)必须在每条支路都如实出现。
        var path = new HashSet<int>();
        return await BuildBackwardNodeAsync(root, viaWorkOrder: null, path, cancellationToken);
    }

    private async Task<LotTraceNode> BuildBackwardNodeAsync(
        MaterialLot lot,
        (int Id, string Code)? viaWorkOrder,
        HashSet<int> path,
        CancellationToken cancellationToken)
    {
        var children = new List<LotTraceNode>();
        path.Add(lot.Id);

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
                if (path.Contains(source.Id))
                {
                    continue;
                }

                children.Add(await BuildBackwardNodeAsync(
                    source, (workOrderId, workOrderCode), path, cancellationToken));
            }
        }

        path.Remove(lot.Id);
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
