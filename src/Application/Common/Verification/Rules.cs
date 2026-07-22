using Lingban.Application.Common.Interfaces;
using Lingban.Application.Equipment.Queries;
using Lingban.Application.Production.Queries;
using Lingban.Application.Quality.Queries;
using Lingban.Domain.Services;

namespace Lingban.Application.Common.Verification;

/// <summary>
/// 校验规则:用 IVerificationQueryService 的原生 SQL 路径复核工具结果。
/// 反"自验自证":时间范围不信任工具 DTO,规则从工厂日历独立重建;
/// 覆盖面不止总数,含状态分布 / 明细 ID 集合 / 公式一致性。
/// </summary>
public class TodayWorkOrdersVerificationRule : IVerificationRule
{
    private readonly IVerificationQueryService _queries;
    private readonly IFactoryCalendarProvider _calendarProvider;

    public TodayWorkOrdersVerificationRule(
        IVerificationQueryService queries, IFactoryCalendarProvider calendarProvider)
    {
        _queries = queries;
        _calendarProvider = calendarProvider;
    }

    public bool Supports(string toolName) => toolName == ToolNames.GetTodayWorkOrders;

    public async Task<VerificationResult> VerifyAsync(object toolResult, CancellationToken cancellationToken)
    {
        if (toolResult is not TodayWorkOrdersDto dto)
        {
            return Mismatched(nameof(TodayWorkOrdersDto));
        }

        // 独立重建生产日边界:工具若退化成 UTC 划天并输出自洽的假边界,这里会抓到。
        ShiftCalendar calendar = await _calendarProvider.GetCalendarAsync(cancellationToken);
        var (fromUtc, toUtc) = calendar.GetProductionDayBoundsUtc(dto.ProductionDate);

        TodayWorkOrderCounts actual = await _queries.CountTodayWorkOrdersAsync(
            fromUtc, toUtc, dto.ProductionLineId, cancellationToken);

        return VerificationResult.FromChecks(new[]
        {
            new VerificationCheck("FromUtc", dto.FromUtc.ToString("O"), fromUtc.ToString("O"), dto.FromUtc == fromUtc),
            new VerificationCheck("ToUtc", dto.ToUtc.ToString("O"), toUtc.ToString("O"), dto.ToUtc == toUtc),
            new VerificationCheck("TotalCount", dto.TotalCount.ToString(), actual.Total.ToString(), dto.TotalCount == actual.Total),
            new VerificationCheck("InProgressCount", dto.InProgressCount.ToString(), actual.InProgress.ToString(), dto.InProgressCount == actual.InProgress),
            new VerificationCheck("CompletedCount", dto.CompletedCount.ToString(), actual.Completed.ToString(), dto.CompletedCount == actual.Completed)
        });
    }

    internal static VerificationResult Mismatched(string expectedType) => new()
    {
        Status = VerificationStatus.Failed,
        Summary = $"Tool result is not a {expectedType}."
    };
}

public class DelayedOrdersVerificationRule : IVerificationRule
{
    private readonly IVerificationQueryService _queries;

    public DelayedOrdersVerificationRule(IVerificationQueryService queries)
    {
        _queries = queries;
    }

    public bool Supports(string toolName) => toolName == ToolNames.AnalyzeDelayedOrders;

    public async Task<VerificationResult> VerifyAsync(object toolResult, CancellationToken cancellationToken)
    {
        if (toolResult is not DelayedOrdersDto dto)
        {
            return TodayWorkOrdersVerificationRule.Mismatched(nameof(DelayedOrdersDto));
        }

        IReadOnlyList<int> actualIds = await _queries.GetDelayedWorkOrderIdsAsync(dto.AsOfUtc, cancellationToken);
        string claimedIds = string.Join(",", dto.Orders.Select(order => order.Id).OrderBy(id => id));
        string actualIdList = string.Join(",", actualIds);

        return VerificationResult.FromChecks(new[]
        {
            new VerificationCheck("TotalCount", dto.TotalCount.ToString(), actualIds.Count.ToString(), dto.TotalCount == actualIds.Count),
            new VerificationCheck("OrderIds", claimedIds, actualIdList, claimedIds == actualIdList)
        });
    }
}

public class DefectSummaryVerificationRule : IVerificationRule
{
    private readonly IVerificationQueryService _queries;

    public DefectSummaryVerificationRule(IVerificationQueryService queries)
    {
        _queries = queries;
    }

    public bool Supports(string toolName) => toolName == ToolNames.GetDefectSummary;

    public async Task<VerificationResult> VerifyAsync(object toolResult, CancellationToken cancellationToken)
    {
        if (toolResult is not DefectSummaryDto dto)
        {
            return TodayWorkOrdersVerificationRule.Mismatched(nameof(DefectSummaryDto));
        }

        decimal actual = await _queries.SumDefectQuantityBetweenAsync(dto.SinceUtc, dto.AsOfUtc, cancellationToken);
        decimal byTypeSum = dto.ByType.Sum(item => item.Quantity);

        return VerificationResult.FromChecks(new[]
        {
            new VerificationCheck("TotalQuantity", dto.TotalQuantity.ToString(), actual.ToString(), dto.TotalQuantity == actual),
            new VerificationCheck("ByTypeSum", byTypeSum.ToString(), dto.TotalQuantity.ToString(), byTypeSum == dto.TotalQuantity)
        });
    }
}

public class OeeVerificationRule : IVerificationRule
{
    private const double MinuteTolerance = 0.01;

    private readonly IVerificationQueryService _queries;
    private readonly IFactoryCalendarProvider _calendarProvider;

    public OeeVerificationRule(IVerificationQueryService queries, IFactoryCalendarProvider calendarProvider)
    {
        _queries = queries;
        _calendarProvider = calendarProvider;
    }

    public bool Supports(string toolName) => toolName == ToolNames.CalculateOee;

    public async Task<VerificationResult> VerifyAsync(object toolResult, CancellationToken cancellationToken)
    {
        if (toolResult is not OeeDto dto)
        {
            return TodayWorkOrdersVerificationRule.Mismatched(nameof(OeeDto));
        }

        // 班次区间从日历独立重建,不信任 DTO 自带的 ShiftPeriods。
        ShiftCalendar calendar = await _calendarProvider.GetCalendarAsync(cancellationToken);
        IReadOnlyList<ShiftPeriod> periods = calendar.GetShiftPeriods(dto.ProductionDate);
        double plannedMinutes = periods.Sum(period => (period.EndUtc - period.StartUtc).TotalMinutes);

        double actualDowntime = await _queries.SumDowntimeUnionMinutesAsync(
            dto.EquipmentId,
            periods.Select(period => (period.StartUtc, period.EndUtc)).ToList(),
            dto.AsOfUtc,
            cancellationToken);

        // Availability 必须与业务公式(含 Running 下限 0 的钳制)一致。
        double expectedAvailability = dto.PlannedMinutes > 0
            ? Math.Max(0d, dto.PlannedMinutes - dto.DowntimeMinutes) / dto.PlannedMinutes
            : 0d;

        return VerificationResult.FromChecks(new[]
        {
            new VerificationCheck(
                "PlannedMinutes",
                dto.PlannedMinutes.ToString("0.##"),
                plannedMinutes.ToString("0.##"),
                Math.Abs(plannedMinutes - dto.PlannedMinutes) <= MinuteTolerance),
            new VerificationCheck(
                "DowntimeMinutes",
                dto.DowntimeMinutes.ToString("0.##"),
                actualDowntime.ToString("0.##"),
                Math.Abs(actualDowntime - dto.DowntimeMinutes) <= MinuteTolerance),
            new VerificationCheck(
                "Availability",
                dto.Availability.ToString("0.####"),
                expectedAvailability.ToString("0.####"),
                Math.Abs(dto.Availability - expectedAvailability) <= 0.0001)
        });
    }
}
