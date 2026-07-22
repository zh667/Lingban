using Lingban.Application.Common.Interfaces;
using Lingban.Domain.Entities.Production;

namespace Lingban.Application.WorkOrders.Commands;

public record ReportProductionCommand : IRequest
{
    public int WorkOrderId { get; init; }

    public decimal Completed { get; init; }

    public decimal Qualified { get; init; }

    public decimal Scrap { get; init; }

    public decimal Rework { get; init; }
}

public class ReportProductionCommandValidator : AbstractValidator<ReportProductionCommand>
{
    public ReportProductionCommandValidator()
    {
        RuleFor(command => command.Completed).GreaterThanOrEqualTo(0);
        RuleFor(command => command.Qualified).GreaterThanOrEqualTo(0);
        RuleFor(command => command.Scrap).GreaterThanOrEqualTo(0);
        RuleFor(command => command.Rework).GreaterThanOrEqualTo(0);
    }
}

public class ReportProductionCommandHandler : IRequestHandler<ReportProductionCommand>
{
    private readonly IApplicationDbContext _context;

    public ReportProductionCommandHandler(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task Handle(ReportProductionCommand request, CancellationToken cancellationToken)
    {
        WorkOrder? order = await _context.WorkOrders
            .FirstOrDefaultAsync(order => order.Id == request.WorkOrderId, cancellationToken);
        Guard.Against.NotFound(request.WorkOrderId, order);

        order.ReportProduction(request.Completed, request.Qualified, request.Scrap, request.Rework);
        await _context.SaveChangesAsync(cancellationToken);
    }
}

public record ProduceLotCommand : IRequest<int>
{
    public int WorkOrderId { get; init; }

    public string LotNumber { get; init; } = string.Empty;

    public decimal Quantity { get; init; }
}

public class ProduceLotCommandValidator : AbstractValidator<ProduceLotCommand>
{
    public ProduceLotCommandValidator()
    {
        RuleFor(command => command.LotNumber).NotEmpty().MaximumLength(64);
        RuleFor(command => command.Quantity).GreaterThan(0);
    }
}

public class ProduceLotCommandHandler : IRequestHandler<ProduceLotCommand, int>
{
    private readonly IApplicationDbContext _context;
    private readonly TimeProvider _timeProvider;

    public ProduceLotCommandHandler(IApplicationDbContext context, TimeProvider timeProvider)
    {
        _context = context;
        _timeProvider = timeProvider;
    }

    public async Task<int> Handle(ProduceLotCommand request, CancellationToken cancellationToken)
    {
        WorkOrder? order = await _context.WorkOrders
            .FirstOrDefaultAsync(order => order.Id == request.WorkOrderId, cancellationToken);
        Guard.Against.NotFound(request.WorkOrderId, order);

        var lot = order.ProduceLot(request.LotNumber, request.Quantity, _timeProvider.GetUtcNow());
        await _context.SaveChangesAsync(cancellationToken);
        return lot.Id;
    }
}

/// <summary>
/// 完工前置校验(债 #7 余下部分):标准工单必须有实记消耗、有产出批次,
/// 且产出批次总量与完工报工一致。"无料工单"暂不放行——出现真实需求时
/// 建显式工单类型,而不是默认绕过校验。
/// </summary>
public record CompleteWorkOrderCommand(int WorkOrderId) : IRequest;

public class CompleteWorkOrderCommandHandler : IRequestHandler<CompleteWorkOrderCommand>
{
    private readonly IApplicationDbContext _context;
    private readonly TimeProvider _timeProvider;

    public CompleteWorkOrderCommandHandler(IApplicationDbContext context, TimeProvider timeProvider)
    {
        _context = context;
        _timeProvider = timeProvider;
    }

    public async Task Handle(CompleteWorkOrderCommand request, CancellationToken cancellationToken)
    {
        WorkOrder? order = await _context.WorkOrders
            .FirstOrDefaultAsync(order => order.Id == request.WorkOrderId, cancellationToken);
        Guard.Against.NotFound(request.WorkOrderId, order);

        bool hasConsumption = await _context.MaterialConsumptions
            .AnyAsync(consumption => consumption.WorkOrderId == order.Id, cancellationToken);
        if (!hasConsumption)
        {
            throw new InvalidOperationException(
                $"Work order {order.Code} cannot complete: no material consumption has been recorded.");
        }

        decimal producedTotal = await _context.MaterialLots
            .Where(lot => lot.ProducedByWorkOrderId == order.Id)
            .SumAsync(lot => (decimal?)lot.InitialQuantity, cancellationToken) ?? 0m;
        if (producedTotal <= 0)
        {
            throw new InvalidOperationException(
                $"Work order {order.Code} cannot complete: no output lot has been produced.");
        }

        if (producedTotal != order.CompletedQuantity)
        {
            throw new InvalidOperationException(
                $"Work order {order.Code} cannot complete: output lots total {producedTotal} " +
                $"does not match reported completed quantity {order.CompletedQuantity}.");
        }

        order.Complete(_timeProvider.GetUtcNow());
        await _context.SaveChangesAsync(cancellationToken);
    }
}
