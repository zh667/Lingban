using Lingban.Application.Common.Interfaces;
using Lingban.Domain.Entities.Materials;
using Lingban.Domain.Entities.Production;

namespace Lingban.Application.WorkOrders.Commands;

/// <summary>
/// 工位实记消耗。
/// 幂等:EventId 重放返回已有记录且校验请求指纹,键碰撞显式报错;唯一索引兜底竞态。
/// 环检测:与写边在同一把租户级事务锁内(IGenealogySerializedExecutor),无 TOCTOU 窗口。
/// </summary>
public record RecordConsumptionCommand : IRequest<int>
{
    public int WorkOrderId { get; init; }

    public int MaterialLotId { get; init; }

    public decimal Quantity { get; init; }

    public int WorkstationId { get; init; }

    /// <summary>调用方生成的幂等键;同一物理事件必须复用同一键。</summary>
    public Guid EventId { get; init; }

    public string? RecordedBy { get; init; }
}

public class RecordConsumptionCommandValidator : AbstractValidator<RecordConsumptionCommand>
{
    public RecordConsumptionCommandValidator()
    {
        RuleFor(command => command.WorkOrderId).GreaterThan(0);
        RuleFor(command => command.MaterialLotId).GreaterThan(0);
        RuleFor(command => command.WorkstationId).GreaterThan(0);
        RuleFor(command => command.Quantity).GreaterThan(0);
        RuleFor(command => command.EventId).NotEmpty();
        RuleFor(command => command.RecordedBy).MaximumLength(128);
    }
}

public class RecordConsumptionCommandHandler : IRequestHandler<RecordConsumptionCommand, int>
{
    private readonly IApplicationDbContext _context;
    private readonly IGenealogySerializedExecutor _serializedExecutor;
    private readonly TimeProvider _timeProvider;

    public RecordConsumptionCommandHandler(
        IApplicationDbContext context,
        IGenealogySerializedExecutor serializedExecutor,
        TimeProvider timeProvider)
    {
        _context = context;
        _serializedExecutor = serializedExecutor;
        _timeProvider = timeProvider;
    }

    public async Task<int> Handle(RecordConsumptionCommand request, CancellationToken cancellationToken)
    {
        try
        {
            return await _serializedExecutor.ExecuteAsync(
                innerToken => HandleSerializedAsync(request, innerToken), cancellationToken);
        }
        catch (DbUpdateException)
        {
            // 唯一索引兜底被击中(理论上闸门已串行化,此为极端竞态后盾):
            // 回滚后按 EventId 复查,存在且指纹一致即幂等成功。
            MaterialConsumption? existing = await FindReplayAsync(request.EventId, cancellationToken);
            if (existing is not null)
            {
                return ValidateFingerprint(existing, request);
            }

            throw;
        }
    }

    private async Task<int> HandleSerializedAsync(RecordConsumptionCommand request, CancellationToken cancellationToken)
    {
        MaterialConsumption? replay = await FindReplayAsync(request.EventId, cancellationToken);
        if (replay is not null)
        {
            return ValidateFingerprint(replay, request);
        }

        WorkOrder? order = await _context.WorkOrders
            .FirstOrDefaultAsync(order => order.Id == request.WorkOrderId, cancellationToken);
        Guard.Against.NotFound(request.WorkOrderId, order);

        MaterialLot? lot = await _context.MaterialLots
            .FirstOrDefaultAsync(lot => lot.Id == request.MaterialLotId, cancellationToken);
        Guard.Against.NotFound(request.MaterialLotId, lot);

        int? stationLineId = await _context.Workstations
            .Where(station => station.Id == request.WorkstationId)
            .Select(station => (int?)station.ProductionLineId)
            .FirstOrDefaultAsync(cancellationToken);
        Guard.Against.NotFound(request.WorkstationId, stationLineId);

        if (stationLineId != order.ProductionLineId)
        {
            throw new InvalidOperationException(
                $"Workstation {request.WorkstationId} belongs to production line {stationLineId}, " +
                $"but work order {order.Code} runs on line {order.ProductionLineId}.");
        }

        if (await IsLotDescendantOfOrderAsync(lot, order.Id, cancellationToken))
        {
            throw new InvalidOperationException(
                $"Consuming lot {lot.LotNumber} into work order {order.Code} would create a genealogy cycle: " +
                "the lot descends from this work order's own output.");
        }

        MaterialConsumption consumption = order.RecordConsumption(
            lot,
            request.Quantity,
            request.WorkstationId,
            _timeProvider.GetUtcNow(),
            request.RecordedBy,
            request.EventId);

        await _context.SaveChangesAsync(cancellationToken);
        return consumption.Id;
    }

    private async Task<MaterialConsumption?> FindReplayAsync(Guid eventId, CancellationToken cancellationToken)
    {
        return await _context.MaterialConsumptions
            .AsNoTracking()
            .FirstOrDefaultAsync(consumption => consumption.EventId == eventId, cancellationToken);
    }

    /// <summary>重放必须与原事件指纹一致;同键不同 payload 是调用方 bug,显式拒绝而不是静默吞掉。</summary>
    private static int ValidateFingerprint(MaterialConsumption existing, RecordConsumptionCommand request)
    {
        bool matches = existing.WorkOrderId == request.WorkOrderId
            && existing.MaterialLotId == request.MaterialLotId
            && existing.Quantity == request.Quantity
            && existing.WorkstationId == request.WorkstationId
            && existing.RecordedBy == request.RecordedBy;

        if (!matches)
        {
            throw new InvalidOperationException(
                $"EventId {request.EventId} was already used for a different consumption " +
                "(work order/lot/quantity/workstation mismatch). Idempotency keys must not be reused.");
        }

        return existing.Id;
    }

    /// <summary>目标批次沿"产出工单 → 该工单的消耗批次"向上走,祖先里出现本工单即为环。</summary>
    private async Task<bool> IsLotDescendantOfOrderAsync(MaterialLot lot, int orderId, CancellationToken cancellationToken)
    {
        var visitedLots = new HashSet<int> { lot.Id };
        List<int> frontier = new() { lot.Id };

        while (frontier.Count > 0)
        {
            List<int> producerOrderIds = await _context.MaterialLots
                .Where(candidate => frontier.Contains(candidate.Id) && candidate.ProducedByWorkOrderId != null)
                .Select(candidate => candidate.ProducedByWorkOrderId!.Value)
                .Distinct()
                .ToListAsync(cancellationToken);

            if (producerOrderIds.Contains(orderId))
            {
                return true;
            }

            if (producerOrderIds.Count == 0)
            {
                return false;
            }

            List<int> parentLotIds = await _context.MaterialConsumptions
                .Where(consumption => producerOrderIds.Contains(consumption.WorkOrderId))
                .Select(consumption => consumption.MaterialLotId)
                .Distinct()
                .ToListAsync(cancellationToken);

            frontier = parentLotIds.Where(visitedLots.Add).ToList();
        }

        return false;
    }
}
