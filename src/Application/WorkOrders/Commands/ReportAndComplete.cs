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
    private readonly IGenealogySerializedExecutor _serializedExecutor;

    public ReportProductionCommandHandler(
        IApplicationDbContext context, IGenealogySerializedExecutor serializedExecutor)
    {
        _context = context;
        _serializedExecutor = serializedExecutor;
    }

    public async Task Handle(ReportProductionCommand request, CancellationToken cancellationToken)
    {
        // 报工与完工共用闸门(Codex 三审 #1):"完工校验通过后又改报工量"的竞态窗口关闭。
        await _serializedExecutor.ExecuteAsync<object?>(async innerToken =>
        {
            WorkOrder? order = await _context.WorkOrders
                .FirstOrDefaultAsync(order => order.Id == request.WorkOrderId, innerToken);
            Guard.Against.NotFound(request.WorkOrderId, order);

            order.ReportProduction(request.Completed, request.Qualified, request.Scrap, request.Rework);
            await _context.SaveChangesAsync(innerToken);
            return null;
        }, cancellationToken);
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
    private readonly IGenealogySerializedExecutor _serializedExecutor;
    private readonly TimeProvider _timeProvider;

    public ProduceLotCommandHandler(
        IApplicationDbContext context,
        IGenealogySerializedExecutor serializedExecutor,
        TimeProvider timeProvider)
    {
        _context = context;
        _serializedExecutor = serializedExecutor;
        _timeProvider = timeProvider;
    }

    public async Task<int> Handle(ProduceLotCommand request, CancellationToken cancellationToken)
    {
        // 产出与完工共用谱系闸门:防止"完工校验通过后又插入产出/消耗"的竞态。
        return await _serializedExecutor.ExecuteAsync(async innerToken =>
        {
            WorkOrder? order = await _context.WorkOrders
                .FirstOrDefaultAsync(order => order.Id == request.WorkOrderId, innerToken);
            Guard.Against.NotFound(request.WorkOrderId, order);

            var lot = order.ProduceLot(request.LotNumber, request.Quantity, _timeProvider.GetUtcNow());
            await _context.SaveChangesAsync(innerToken);
            return lot.Id;
        }, cancellationToken);
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
    private readonly IGenealogySerializedExecutor _serializedExecutor;
    private readonly TimeProvider _timeProvider;

    public CompleteWorkOrderCommandHandler(
        IApplicationDbContext context,
        IGenealogySerializedExecutor serializedExecutor,
        TimeProvider timeProvider)
    {
        _context = context;
        _serializedExecutor = serializedExecutor;
        _timeProvider = timeProvider;
    }

    public async Task Handle(CompleteWorkOrderCommand request, CancellationToken cancellationToken)
    {
        // 校验与状态转换在谱系闸门内:完工后无法再并发插入消耗/产出(它们也走同一把锁)。
        await _serializedExecutor.ExecuteAsync<object?>(async innerToken =>
        {
            await HandleSerializedAsync(request, innerToken);
            return null;
        }, cancellationToken);
    }

    private async Task HandleSerializedAsync(CompleteWorkOrderCommand request, CancellationToken cancellationToken)
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
