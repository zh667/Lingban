using Lingban.Application.Common.Interfaces;
using Lingban.Domain.Enums;

namespace Lingban.Application.Production.Queries;

public record DelayedOrderDto(
    int Id,
    string Code,
    string ProductCode,
    string ProductionLineCode,
    WorkOrderStatus Status,
    DateTimeOffset PlannedEndUtc,
    double DelayHours,
    decimal PlannedQuantity,
    decimal CompletedQuantity);

public record DelayedOrdersDto(
    DateTimeOffset AsOfUtc,
    int? ProductionLineId,
    int TotalCount,
    IReadOnlyList<DelayedOrderDto> Orders);

/// <summary>延期工单:未完结且计划结束时间已过;可按产线(ID 或编码)过滤。</summary>
public record AnalyzeDelayedOrdersQuery(
    DateTimeOffset? AsOfUtc = null,
    int? ProductionLineId = null,
    string? ProductionLineCode = null) : IRequest<DelayedOrdersDto>;

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

        int? lineId = request.ProductionLineId;
        if (lineId is null && !string.IsNullOrWhiteSpace(request.ProductionLineCode))
        {
            lineId = await _context.ProductionLines
                .Where(line => line.Code == request.ProductionLineCode)
                .Select(line => (int?)line.Id)
                .FirstOrDefaultAsync(cancellationToken);
            Guard.Against.NotFound(request.ProductionLineCode, lineId);
        }

        List<DelayedOrderDto> orders = await _context.WorkOrders.AsNoTracking()
            .Where(order =>
                order.Status != WorkOrderStatus.Completed &&
                order.Status != WorkOrderStatus.Cancelled &&
                order.PlannedEndUtc != null &&
                order.PlannedEndUtc < asOf &&
                (lineId == null || order.ProductionLineId == lineId))
            .OrderBy(order => order.PlannedEndUtc)
            .Select(order => new DelayedOrderDto(
                order.Id,
                order.Code,
                order.Product.Code,
                order.ProductionLine.Code,
                order.Status,
                order.PlannedEndUtc!.Value,
                (asOf - order.PlannedEndUtc!.Value).TotalHours,
                order.PlannedQuantity,
                order.CompletedQuantity))
            .ToListAsync(cancellationToken);

        return new DelayedOrdersDto(asOf, lineId, orders.Count, orders);
    }
}
