using Lingban.Application.Common.Interfaces;
using Lingban.Domain.Entities.Production;

namespace Lingban.Application.WorkOrders.Commands;

// 工单生命周期命令。状态守卫在领域层(WorkOrder 状态机),
// 这里只做加载、编排与需要查库的前置校验。

public record CreateWorkOrderCommand : IRequest<int>
{
    public string Code { get; init; } = string.Empty;

    public int ProductId { get; init; }

    public int ProductionLineId { get; init; }

    public decimal PlannedQuantity { get; init; }

    public string UnitOfMeasure { get; init; } = "PCS";

    public DateTimeOffset? PlannedStartUtc { get; init; }

    public DateTimeOffset? PlannedEndUtc { get; init; }
}

public class CreateWorkOrderCommandValidator : AbstractValidator<CreateWorkOrderCommand>
{
    public CreateWorkOrderCommandValidator()
    {
        RuleFor(command => command.Code).NotEmpty().MaximumLength(64);
        RuleFor(command => command.PlannedQuantity).GreaterThan(0);
        RuleFor(command => command.UnitOfMeasure).NotEmpty().MaximumLength(16);
    }
}

public class CreateWorkOrderCommandHandler : IRequestHandler<CreateWorkOrderCommand, int>
{
    private readonly IApplicationDbContext _context;

    public CreateWorkOrderCommandHandler(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<int> Handle(CreateWorkOrderCommand request, CancellationToken cancellationToken)
    {
        bool productExists = await _context.Products
            .AnyAsync(product => product.Id == request.ProductId, cancellationToken);
        Guard.Against.NotFound(request.ProductId, productExists ? request.ProductId : (int?)null);

        bool lineExists = await _context.ProductionLines
            .AnyAsync(line => line.Id == request.ProductionLineId, cancellationToken);
        Guard.Against.NotFound(request.ProductionLineId, lineExists ? request.ProductionLineId : (int?)null);

        var order = WorkOrder.Create(
            request.Code,
            request.ProductId,
            request.ProductionLineId,
            request.PlannedQuantity,
            request.UnitOfMeasure);
        order.PlannedStartUtc = request.PlannedStartUtc?.ToUniversalTime();
        order.PlannedEndUtc = request.PlannedEndUtc?.ToUniversalTime();

        _context.WorkOrders.Add(order);
        await _context.SaveChangesAsync(cancellationToken);
        return order.Id;
    }
}

public record ReleaseWorkOrderCommand(int WorkOrderId) : IRequest;

public class ReleaseWorkOrderCommandHandler : IRequestHandler<ReleaseWorkOrderCommand>
{
    private readonly IApplicationDbContext _context;

    public ReleaseWorkOrderCommandHandler(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task Handle(ReleaseWorkOrderCommand request, CancellationToken cancellationToken)
    {
        WorkOrder? order = await _context.WorkOrders
            .FirstOrDefaultAsync(order => order.Id == request.WorkOrderId, cancellationToken);
        Guard.Against.NotFound(request.WorkOrderId, order);

        order.Release();
        await _context.SaveChangesAsync(cancellationToken);
    }
}

public record StartWorkOrderCommand(int WorkOrderId) : IRequest;

public class StartWorkOrderCommandHandler : IRequestHandler<StartWorkOrderCommand>
{
    private readonly IApplicationDbContext _context;
    private readonly TimeProvider _timeProvider;

    public StartWorkOrderCommandHandler(IApplicationDbContext context, TimeProvider timeProvider)
    {
        _context = context;
        _timeProvider = timeProvider;
    }

    public async Task Handle(StartWorkOrderCommand request, CancellationToken cancellationToken)
    {
        WorkOrder? order = await _context.WorkOrders
            .FirstOrDefaultAsync(order => order.Id == request.WorkOrderId, cancellationToken);
        Guard.Against.NotFound(request.WorkOrderId, order);

        order.Start(_timeProvider.GetUtcNow());
        await _context.SaveChangesAsync(cancellationToken);
    }
}

public record CancelWorkOrderCommand(int WorkOrderId) : IRequest;

public class CancelWorkOrderCommandHandler : IRequestHandler<CancelWorkOrderCommand>
{
    private readonly IApplicationDbContext _context;

    public CancelWorkOrderCommandHandler(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task Handle(CancelWorkOrderCommand request, CancellationToken cancellationToken)
    {
        WorkOrder? order = await _context.WorkOrders
            .FirstOrDefaultAsync(order => order.Id == request.WorkOrderId, cancellationToken);
        Guard.Against.NotFound(request.WorkOrderId, order);

        order.Cancel();
        await _context.SaveChangesAsync(cancellationToken);
    }
}
