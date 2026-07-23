using Lingban.Application.Common.Interfaces;
using Lingban.Domain.Enums;
using Lingban.Domain.Services;

namespace Lingban.Application.Production.Queries;

public record WorkOrderSummaryDto(
    int Id,
    string Code,
    string ProductCode,
    string ProductName,
    WorkOrderStatus Status,
    decimal PlannedQuantity,
    decimal CompletedQuantity,
    decimal QualifiedQuantity,
    decimal ScrapQuantity,
    bool IsOverproduced);

public record TodayWorkOrdersDto(
    DateOnly ProductionDate,
    int? ProductionLineId,
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    IReadOnlyList<WorkOrderSummaryDto> Orders,
    int TotalCount,
    int InProgressCount,
    int CompletedCount);

/// <summary>
/// "今天"的工单:生产日按工厂日历与班次切分(旧项目 UTC 划天的病根治点)。
/// AsOfUtc 缺省为当前时刻,测试与回放可显式传入。
/// </summary>
public record GetTodayWorkOrdersQuery(
    int? ProductionLineId = null,
    DateTimeOffset? AsOfUtc = null,
    string? ProductionLineCode = null) : IRequest<TodayWorkOrdersDto>;

public class GetTodayWorkOrdersQueryValidator : AbstractValidator<GetTodayWorkOrdersQuery>
{
    public GetTodayWorkOrdersQueryValidator()
    {
        RuleFor(query => query.ProductionLineId).GreaterThan(0).When(query => query.ProductionLineId.HasValue);
    }
}

public class GetTodayWorkOrdersQueryHandler : IRequestHandler<GetTodayWorkOrdersQuery, TodayWorkOrdersDto>
{
    private readonly IApplicationDbContext _context;
    private readonly IFactoryCalendarProvider _calendarProvider;
    private readonly TimeProvider _timeProvider;

    public GetTodayWorkOrdersQueryHandler(
        IApplicationDbContext context,
        IFactoryCalendarProvider calendarProvider,
        TimeProvider timeProvider)
    {
        _context = context;
        _calendarProvider = calendarProvider;
        _timeProvider = timeProvider;
    }

    public async Task<TodayWorkOrdersDto> Handle(GetTodayWorkOrdersQuery request, CancellationToken cancellationToken)
    {
        DateTimeOffset asOf = request.AsOfUtc ?? _timeProvider.GetUtcNow();
        int? lineId = await ResolveLineAsync(request, cancellationToken);
        ShiftCalendar calendar = await _calendarProvider.GetCalendarAsync(cancellationToken);
        DateOnly productionDate = calendar.GetProductionDate(asOf);
        var (fromUtc, toUtc) = calendar.GetProductionDayBoundsUtc(productionDate);

        var query = _context.WorkOrders.AsNoTracking()
            .Where(order =>
                (order.ActualStartUtc >= fromUtc && order.ActualStartUtc < toUtc) ||
                (order.ActualStartUtc == null && order.PlannedStartUtc >= fromUtc && order.PlannedStartUtc < toUtc));

        if (lineId is int resolvedLineId)
        {
            query = query.Where(order => order.ProductionLineId == resolvedLineId);
        }

        List<WorkOrderSummaryDto> orders = await query
            .OrderBy(order => order.Code)
            .Select(order => new WorkOrderSummaryDto(
                order.Id,
                order.Code,
                order.Product.Code,
                order.Product.Name,
                order.Status,
                order.PlannedQuantity,
                order.CompletedQuantity,
                order.QualifiedQuantity,
                order.ScrapQuantity,
                order.CompletedQuantity > order.PlannedQuantity))
            .ToListAsync(cancellationToken);

        return new TodayWorkOrdersDto(
            productionDate,
            lineId,
            fromUtc,
            toUtc,
            orders,
            orders.Count,
            orders.Count(order => order.Status == WorkOrderStatus.InProgress),
            orders.Count(order => order.Status == WorkOrderStatus.Completed));
    }

    private async Task<int?> ResolveLineAsync(GetTodayWorkOrdersQuery request, CancellationToken cancellationToken)
    {
        if (request.ProductionLineId is int id)
        {
            return id;
        }

        if (string.IsNullOrWhiteSpace(request.ProductionLineCode))
        {
            return null;
        }

        int? resolved = await _context.ProductionLines
            .Where(line => line.Code == request.ProductionLineCode)
            .Select(line => (int?)line.Id)
            .FirstOrDefaultAsync(cancellationToken);
        Guard.Against.NotFound(request.ProductionLineCode, resolved);
        return resolved;
    }
}
