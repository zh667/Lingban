using Lingban.Application.Common.Interfaces;
using Lingban.Domain.Enums;

namespace Lingban.Application.Production.Queries;

public record DelayedOrderDto(
    int Id,
    string Code,
    string ProductCode,
    WorkOrderStatus Status,
    DateTimeOffset PlannedEndUtc,
    double DelayHours,
    decimal PlannedQuantity,
    decimal CompletedQuantity);

public record DelayedOrdersDto(
    DateTimeOffset AsOfUtc,
    int TotalCount,
    IReadOnlyList<DelayedOrderDto> Orders);

/// <summary>延期工单:未完结且计划结束时间已过。</summary>
public record AnalyzeDelayedOrdersQuery(DateTimeOffset? AsOfUtc = null) : IRequest<DelayedOrdersDto>;

public class AnalyzeDelayedOrdersQueryHandler : IRequestHandler<AnalyzeDelayedOrdersQuery, DelayedOrdersDto>
{
    private readonly IApplicationDbContext _context;
    private readonly TimeProvider _timeProvider;

    public AnalyzeDelayedOrdersQueryHandler(IApplicationDbContext context, TimeProvider timeProvider)
    {
        _context = context;
        _timeProvider = timeProvider;
    }

    public async Task<DelayedOrdersDto> Handle(AnalyzeDelayedOrdersQuery request, CancellationToken cancellationToken)
    {
        DateTimeOffset asOf = request.AsOfUtc ?? _timeProvider.GetUtcNow();

        List<DelayedOrderDto> orders = await _context.WorkOrders.AsNoTracking()
            .Where(order =>
                order.Status != WorkOrderStatus.Completed &&
                order.Status != WorkOrderStatus.Cancelled &&
                order.PlannedEndUtc != null &&
                order.PlannedEndUtc < asOf)
            .OrderBy(order => order.PlannedEndUtc)
            .Select(order => new DelayedOrderDto(
                order.Id,
                order.Code,
                order.Product.Code,
                order.Status,
                order.PlannedEndUtc!.Value,
                (asOf - order.PlannedEndUtc!.Value).TotalHours,
                order.PlannedQuantity,
                order.CompletedQuantity))
            .ToListAsync(cancellationToken);

        return new DelayedOrdersDto(asOf, orders.Count, orders);
    }
}
