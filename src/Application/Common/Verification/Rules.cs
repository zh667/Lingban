using Lingban.Application.Common.Interfaces;
using Lingban.Application.Equipment.Queries;
using Lingban.Application.Production.Queries;
using Lingban.Application.Quality.Queries;
using Lingban.Domain.Services;

namespace Lingban.Application.Common.Verification;

/// <summary>
/// 校验规则:时间范围一律从原始工具请求独立推导(不信任 DTO 自报的范围),
/// 数字经 IVerificationQueryService 的原生 SQL 路径复核。
/// 请求未钉死 AsOfUtc 时无法复现工具的取值时刻,如实返回 Unverified——
/// M3 的 Agent 循环调用工具前必须先钉死 AsOfUtc。
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

    public async Task<VerificationResult> VerifyAsync(
        object toolRequest, object toolResult, CancellationToken cancellationToken)
    {
        if (toolResult is not TodayWorkOrdersDto dto)
        {
            return Mismatched(nameof(TodayWorkOrdersDto));
        }

        if (toolRequest is not GetTodayWorkOrdersQuery query)
        {
            return MissingContext(nameof(GetTodayWorkOrdersQuery));
        }

        if (query.AsOfUtc is not DateTimeOffset asOf)
        {
            return UnpinnedAsOf();
        }

        // 生产日与边界全部从请求重推:工具选错生产日或伪造自洽边界都会在此暴露。
        ShiftCalendar calendar = await _calendarProvider.GetCalendarAsync(cancellationToken);
        DateOnly expectedDate = calendar.GetProductionDate(asOf);
        var (fromUtc, toUtc) = calendar.GetProductionDayBoundsUtc(expectedDate);

        TodayWorkOrderCounts actual = await _queries.CountTodayWorkOrdersAsync(
            fromUtc, toUtc, query.ProductionLineId, cancellationToken);

        return VerificationResult.FromChecks(new[]
        {
            new VerificationCheck("ProductionDate", dto.ProductionDate.ToString("O"), expectedDate.ToString("O"), dto.ProductionDate == expectedDate),
            new VerificationCheck("ProductionLineId", dto.ProductionLineId?.ToString() ?? "null", query.ProductionLineId?.ToString() ?? "null", dto.ProductionLineId == query.ProductionLineId),
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

    internal static VerificationResult MissingContext(string expectedType) => new()
    {
        Status = VerificationStatus.Failed,
        Summary = $"Verification requires the original tool request ({expectedType}) as invocation context."
    };

    internal static VerificationResult UnpinnedAsOf() => new()
    {
        Status = VerificationStatus.Unverified,
        Summary = "Verification requires a pinned AsOfUtc in the tool request; " +
                  "the agent loop must resolve 'now' before dispatching the tool."
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

    public async Task<VerificationResult> VerifyAsync(
        object toolRequest, object toolResult, CancellationToken cancellationToken)
    {
        if (toolResult is not DelayedOrdersDto dto)
        {
            return TodayWorkOrdersVerificationRule.Mismatched(nameof(DelayedOrdersDto));
        }

        if (toolRequest is not AnalyzeDelayedOrdersQuery query)
        {
            return TodayWorkOrdersVerificationRule.MissingContext(nameof(AnalyzeDelayedOrdersQuery));
        }

        if (query.AsOfUtc is not DateTimeOffset asOf)
        {
            return TodayWorkOrdersVerificationRule.UnpinnedAsOf();
        }

        IReadOnlyList<int> actualIds = await _queries.GetDelayedWorkOrderIdsAsync(asOf, cancellationToken);
        string claimedIds = string.Join(",", dto.Orders.Select(order => order.Id).OrderBy(id => id));
        string actualIdList = string.Join(",", actualIds);

        return VerificationResult.FromChecks(new[]
        {
            new VerificationCheck("AsOfUtc", dto.AsOfUtc.ToString("O"), asOf.ToString("O"), dto.AsOfUtc == asOf),
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

    public async Task<VerificationResult> VerifyAsync(
        object toolRequest, object toolResult, CancellationToken cancellationToken)
    {
        if (toolResult is not DefectSummaryDto dto)
        {
            return TodayWorkOrdersVerificationRule.Mismatched(nameof(DefectSummaryDto));
        }

        if (toolRequest is not GetDefectSummaryQuery query)
        {
            return TodayWorkOrdersVerificationRule.MissingContext(nameof(GetDefectSummaryQuery));
        }

        if (query.AsOfUtc is not DateTimeOffset asOf)
        {
            return TodayWorkOrdersVerificationRule.UnpinnedAsOf();
        }

        DateTimeOffset expectedSince = asOf.AddDays(-query.Days);
        decimal actual = await _queries.SumDefectQuantityBetweenAsync(expectedSince, asOf, cancellationToken);
        decimal byTypeSum = dto.ByType.Sum(item => item.Quantity);

        return VerificationResult.FromChecks(new[]
        {
            new VerificationCheck("SinceUtc", dto.SinceUtc.ToString("O"), expectedSince.ToString("O"), dto.SinceUtc == expectedSince),
            new VerificationCheck("AsOfUtc", dto.AsOfUtc.ToString("O"), asOf.ToString("O"), dto.AsOfUtc == asOf),
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

    public async Task<VerificationResult> VerifyAsync(
        object toolRequest, object toolResult, CancellationToken cancellationToken)
    {
        if (toolResult is not OeeDto dto)
        {
            return TodayWorkOrdersVerificationRule.Mismatched(nameof(OeeDto));
        }

        if (toolRequest is not CalculateOeeQuery query)
        {
            return TodayWorkOrdersVerificationRule.MissingContext(nameof(CalculateOeeQuery));
        }

        if (query.AsOfUtc is not DateTimeOffset asOf)
        {
            return TodayWorkOrdersVerificationRule.UnpinnedAsOf();
        }

        // 生产日与班次区间从请求 + 日历独立重建;停机复核用同一 asOf 截断。
        ShiftCalendar calendar = await _calendarProvider.GetCalendarAsync(cancellationToken);
        DateOnly expectedDate = query.ProductionDate ?? calendar.GetProductionDate(asOf);
        IReadOnlyList<ShiftPeriod> periods = calendar.GetShiftPeriods(expectedDate);
        double plannedMinutes = periods.Sum(period => (period.EndUtc - period.StartUtc).TotalMinutes);

        double actualDowntime = await _queries.SumDowntimeUnionMinutesAsync(
            query.EquipmentId,
            periods.Select(period => (period.StartUtc, period.EndUtc)).ToList(),
            asOf,
            cancellationToken);

        double expectedAvailability = dto.PlannedMinutes > 0
            ? Math.Max(0d, dto.PlannedMinutes - dto.DowntimeMinutes) / dto.PlannedMinutes
            : 0d;

        return VerificationResult.FromChecks(new[]
        {
            new VerificationCheck("EquipmentId", dto.EquipmentId.ToString(), query.EquipmentId.ToString(), dto.EquipmentId == query.EquipmentId),
            new VerificationCheck("ProductionDate", dto.ProductionDate.ToString("O"), expectedDate.ToString("O"), dto.ProductionDate == expectedDate),
            new VerificationCheck("AsOfUtc", dto.AsOfUtc.ToString("O"), asOf.ToString("O"), dto.AsOfUtc == asOf),
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
