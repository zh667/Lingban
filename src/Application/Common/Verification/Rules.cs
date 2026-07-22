using Lingban.Application.Equipment.Queries;
using Lingban.Application.Production.Queries;
using Lingban.Application.Quality.Queries;

namespace Lingban.Application.Common.Verification;

/// <summary>
/// 校验规则:用 IVerificationQueryService 的原生 SQL 路径复核工具结果的关键数字。
/// 数值容差:计数精确相等;分钟数容差 0.01。
/// </summary>
public class TodayWorkOrdersVerificationRule : IVerificationRule
{
    private readonly IVerificationQueryService _queries;

    public TodayWorkOrdersVerificationRule(IVerificationQueryService queries)
    {
        _queries = queries;
    }

    public bool Supports(string toolName) => toolName == ToolNames.GetTodayWorkOrders;

    public async Task<VerificationResult> VerifyAsync(object toolResult, CancellationToken cancellationToken)
    {
        if (toolResult is not TodayWorkOrdersDto dto)
        {
            return Mismatched(nameof(TodayWorkOrdersDto));
        }

        int actual = await _queries.CountWorkOrdersStartedBetweenAsync(dto.FromUtc, dto.ToUtc, cancellationToken);
        return VerificationResult.FromChecks(new[]
        {
            new VerificationCheck("TotalCount", dto.TotalCount.ToString(), actual.ToString(), dto.TotalCount == actual)
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

        int actual = await _queries.CountDelayedWorkOrdersAsync(dto.AsOfUtc, cancellationToken);
        return VerificationResult.FromChecks(new[]
        {
            new VerificationCheck("TotalCount", dto.TotalCount.ToString(), actual.ToString(), dto.TotalCount == actual)
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

        decimal actual = await _queries.SumDefectQuantitySinceAsync(dto.SinceUtc, cancellationToken);
        return VerificationResult.FromChecks(new[]
        {
            new VerificationCheck("TotalQuantity", dto.TotalQuantity.ToString(), actual.ToString(), dto.TotalQuantity == actual)
        });
    }
}

public class OeeVerificationRule : IVerificationRule
{
    private const double MinuteTolerance = 0.01;

    private readonly IVerificationQueryService _queries;

    public OeeVerificationRule(IVerificationQueryService queries)
    {
        _queries = queries;
    }

    public bool Supports(string toolName) => toolName == ToolNames.CalculateOee;

    public async Task<VerificationResult> VerifyAsync(object toolResult, CancellationToken cancellationToken)
    {
        if (toolResult is not OeeDto dto)
        {
            return TodayWorkOrdersVerificationRule.Mismatched(nameof(OeeDto));
        }

        // 按班次区间逐段复核停机分钟(独立 SQL 求交),空档停机不计入。
        double actualDowntime = 0d;
        foreach (ShiftPeriodDto period in dto.ShiftPeriods)
        {
            actualDowntime += await _queries.SumDowntimeMinutesAsync(
                dto.EquipmentId, period.StartUtc, period.EndUtc, cancellationToken);
        }

        bool downtimeMatches = Math.Abs(actualDowntime - dto.DowntimeMinutes) <= MinuteTolerance;
        bool availabilityConsistent = dto.PlannedMinutes <= 0 ||
            Math.Abs(dto.Availability - (dto.PlannedMinutes - dto.DowntimeMinutes) / dto.PlannedMinutes) <= 0.0001;

        return VerificationResult.FromChecks(new[]
        {
            new VerificationCheck(
                "DowntimeMinutes",
                dto.DowntimeMinutes.ToString("0.##"),
                actualDowntime.ToString("0.##"),
                downtimeMatches),
            new VerificationCheck(
                "Availability",
                dto.Availability.ToString("0.####"),
                "planned/downtime consistency",
                availabilityConsistent)
        });
    }
}
