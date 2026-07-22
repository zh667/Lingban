using Lingban.Application.Common.Interfaces;
using Lingban.Domain.Entities.Materials;
using Lingban.Domain.Entities.Production;

namespace Lingban.Application.WorkOrders.Commands;

/// <summary>
/// 工位实记消耗。
/// 幂等:EventId 重放返回已有记录,不二次扣料(债 #8);
/// 环检测:目标批次的谱系祖先若由本工单产出,拒绝写入(债 #3 余下部分)。
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
        RuleFor(command => command.Quantity).GreaterThan(0);
        RuleFor(command => command.EventId).NotEmpty();
    }
}

public class RecordConsumptionCommandHandler : IRequestHandler<RecordConsumptionCommand, int>
{
    private readonly IApplicationDbContext _context;
    private readonly TimeProvider _timeProvider;

    public RecordConsumptionCommandHandler(IApplicationDbContext context, TimeProvider timeProvider)
    {
        _context = context;
        _timeProvider = timeProvider;
    }

    public async Task<int> Handle(RecordConsumptionCommand request, CancellationToken cancellationToken)
    {
        MaterialConsumption? replay = await _context.MaterialConsumptions
            .AsNoTracking()
            .FirstOrDefaultAsync(consumption => consumption.EventId == request.EventId, cancellationToken);
        if (replay is not null)
        {
            return replay.Id;
        }

        WorkOrder? order = await _context.WorkOrders
            .FirstOrDefaultAsync(order => order.Id == request.WorkOrderId, cancellationToken);
        Guard.Against.NotFound(request.WorkOrderId, order);

        MaterialLot? lot = await _context.MaterialLots
            .FirstOrDefaultAsync(lot => lot.Id == request.MaterialLotId, cancellationToken);
        Guard.Against.NotFound(request.MaterialLotId, lot);

        bool stationExists = await _context.Workstations
            .AnyAsync(station => station.Id == request.WorkstationId, cancellationToken);
        Guard.Against.NotFound(request.WorkstationId, stationExists ? request.WorkstationId : (int?)null);

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
