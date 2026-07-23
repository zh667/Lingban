using Lingban.Application.Common.Interfaces;
using Lingban.Application.Equipment.Queries;
using Lingban.Application.Knowledge.Queries;
using Lingban.Application.Production.Queries;
using Lingban.Application.Quality.Queries;
using Lingban.Domain.Services;

namespace Lingban.Application.Common.Verification;

/// <summary>
/// 校验规则(四审后字段级版本,债 #8):时间范围与过滤条件一律从原始请求独立推导,
/// 所有会进入 LLM 上下文的数字字段逐项/逐行复核;请求未钉死 AsOfUtc 时如实返回 Unverified。
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

        int? lineId = query.ProductionLineId;
        if (lineId is null && !string.IsNullOrWhiteSpace(query.ProductionLineCode))
        {
            lineId = await _queries.ResolveProductionLineIdByCodeAsync(query.ProductionLineCode, cancellationToken);
            if (lineId is null)
            {
                return new VerificationResult
                {
                    Status = VerificationStatus.Discrepancy,
                    Summary = $"Production line code '{query.ProductionLineCode}' does not resolve to a line."
                };
            }
        }

        ShiftCalendar calendar = await _calendarProvider.GetCalendarAsync(cancellationToken);
        DateOnly expectedDate = calendar.GetProductionDate(asOf);
        var (fromUtc, toUtc) = calendar.GetProductionDayBoundsUtc(expectedDate);

        TodayWorkOrderCounts actual = await _queries.CountTodayWorkOrdersAsync(
            fromUtc, toUtc, lineId, cancellationToken);

        decimal dtoPlanned = dto.Orders.Sum(order => order.PlannedQuantity);
        decimal dtoCompleted = dto.Orders.Sum(order => order.CompletedQuantity);
        decimal dtoQualified = dto.Orders.Sum(order => order.QualifiedQuantity);
        decimal dtoScrap = dto.Orders.Sum(order => order.ScrapQuantity);

        return VerificationResult.FromChecks(new[]
        {
            new VerificationCheck("ProductionDate", dto.ProductionDate.ToString("O"), expectedDate.ToString("O"), dto.ProductionDate == expectedDate),
            new VerificationCheck("ProductionLineId", dto.ProductionLineId?.ToString() ?? "null", lineId?.ToString() ?? "null", dto.ProductionLineId == lineId),
            new VerificationCheck("FromUtc", dto.FromUtc.ToString("O"), fromUtc.ToString("O"), dto.FromUtc == fromUtc),
            new VerificationCheck("ToUtc", dto.ToUtc.ToString("O"), toUtc.ToString("O"), dto.ToUtc == toUtc),
            new VerificationCheck("TotalCount", dto.TotalCount.ToString(), actual.Total.ToString(), dto.TotalCount == actual.Total),
            new VerificationCheck("InProgressCount", dto.InProgressCount.ToString(), actual.InProgress.ToString(), dto.InProgressCount == actual.InProgress),
            new VerificationCheck("CompletedCount", dto.CompletedCount.ToString(), actual.Completed.ToString(), dto.CompletedCount == actual.Completed),
            new VerificationCheck("PlannedQuantitySum", dtoPlanned.ToString(), actual.PlannedSum.ToString(), dtoPlanned == actual.PlannedSum),
            new VerificationCheck("CompletedQuantitySum", dtoCompleted.ToString(), actual.CompletedSum.ToString(), dtoCompleted == actual.CompletedSum),
            new VerificationCheck("QualifiedQuantitySum", dtoQualified.ToString(), actual.QualifiedSum.ToString(), dtoQualified == actual.QualifiedSum),
            new VerificationCheck("ScrapQuantitySum", dtoScrap.ToString(), actual.ScrapSum.ToString(), dtoScrap == actual.ScrapSum)
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

        int? lineId = query.ProductionLineId;
        if (lineId is null && !string.IsNullOrWhiteSpace(query.ProductionLineCode))
        {
            lineId = await _queries.ResolveProductionLineIdByCodeAsync(query.ProductionLineCode, cancellationToken);
            if (lineId is null)
            {
                return new VerificationResult
                {
                    Status = VerificationStatus.Discrepancy,
                    Summary = $"Production line code '{query.ProductionLineCode}' does not resolve to a line."
                };
            }
        }

        IReadOnlyList<DelayedOrderRow> rows = await _queries.GetDelayedWorkOrderRowsAsync(asOf, lineId, cancellationToken);
        var rowById = rows.ToDictionary(row => row.Id);

        var checks = new List<VerificationCheck>
        {
            new("AsOfUtc", dto.AsOfUtc.ToString("O"), asOf.ToString("O"), dto.AsOfUtc == asOf),
            new("ProductionLineId", dto.ProductionLineId?.ToString() ?? "null", lineId?.ToString() ?? "null", dto.ProductionLineId == lineId),
            new("TotalCount", dto.TotalCount.ToString(), rows.Count.ToString(), dto.TotalCount == rows.Count),
            new(
                "OrderIds",
                string.Join(",", dto.Orders.Select(order => order.Id).OrderBy(id => id)),
                string.Join(",", rows.Select(row => row.Id)),
                dto.Orders.Select(order => order.Id).OrderBy(id => id).SequenceEqual(rows.Select(row => row.Id)))
        };

        // 逐行复核:计划结束/延期小时/数量,每一个都可能进入答案。
        foreach (DelayedOrderDto order in dto.Orders)
        {
            if (!rowById.TryGetValue(order.Id, out DelayedOrderRow? row))
            {
                continue; // OrderIds 检查已经抓到集合差异。
            }

            bool fieldsMatch = order.PlannedEndUtc == row.PlannedEndUtc
                && order.PlannedQuantity == row.PlannedQuantity
                && order.CompletedQuantity == row.CompletedQuantity
                && Math.Abs(order.DelayHours - (asOf - row.PlannedEndUtc).TotalHours) <= 0.01;
            checks.Add(new VerificationCheck(
                $"Order[{order.Id}]",
                $"{order.PlannedEndUtc:O}/{order.DelayHours:0.##}h/{order.PlannedQuantity}/{order.CompletedQuantity}",
                $"{row.PlannedEndUtc:O}/{(asOf - row.PlannedEndUtc).TotalHours:0.##}h/{row.PlannedQuantity}/{row.CompletedQuantity}",
                fieldsMatch));
        }

        return VerificationResult.FromChecks(checks);
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
        IReadOnlyList<DefectTypeRow> rows = await _queries.GetDefectTypeRowsAsync(expectedSince, asOf, cancellationToken);
        decimal actualTotal = rows.Sum(row => row.Quantity);

        var checks = new List<VerificationCheck>
        {
            new("SinceUtc", dto.SinceUtc.ToString("O"), expectedSince.ToString("O"), dto.SinceUtc == expectedSince),
            new("AsOfUtc", dto.AsOfUtc.ToString("O"), asOf.ToString("O"), dto.AsOfUtc == asOf),
            new("TotalQuantity", dto.TotalQuantity.ToString(), actualTotal.ToString(), dto.TotalQuantity == actualTotal),
            new(
                "TypeCodes",
                string.Join(",", dto.ByType.Select(item => item.Code).OrderBy(code => code)),
                string.Join(",", rows.Select(row => row.Code)),
                dto.ByType.Select(item => item.Code).OrderBy(code => code).SequenceEqual(rows.Select(row => row.Code)))
        };

        var rowByCode = rows.ToDictionary(row => row.Code);
        foreach (DefectTypeSummaryDto item in dto.ByType)
        {
            if (!rowByCode.TryGetValue(item.Code, out DefectTypeRow? row))
            {
                continue;
            }

            double expectedShare = actualTotal > 0 ? (double)(row.Quantity / actualTotal) : 0d;
            bool match = item.Quantity == row.Quantity && Math.Abs(item.Share - expectedShare) <= 0.0001;
            checks.Add(new VerificationCheck(
                $"Type[{item.Code}]",
                $"{item.Quantity}@{item.Share:0.####}",
                $"{row.Quantity}@{expectedShare:0.####}",
                match));
        }

        return VerificationResult.FromChecks(checks);
    }
}

/// <summary>
/// 知识检索校验:每个返回分块的文本与文档标题经独立 SQL 核对(内容完整性);
/// 相似度排名依赖同一 embedding,无法独立复核——如实降为不校验项,不假装。
/// </summary>
public class KnowledgeSearchVerificationRule : IVerificationRule
{
    private readonly IVerificationQueryService _queries;

    public KnowledgeSearchVerificationRule(IVerificationQueryService queries) => _queries = queries;

    public bool Supports(string toolName) => toolName == ToolNames.SearchKnowledge;

    public async Task<VerificationResult> VerifyAsync(
        object toolRequest, object toolResult, CancellationToken cancellationToken)
    {
        if (toolResult is not KnowledgeSearchResultDto dto)
        {
            return TodayWorkOrdersVerificationRule.Mismatched(nameof(KnowledgeSearchResultDto));
        }

        if (toolRequest is not SearchKnowledgeQuery query)
        {
            return TodayWorkOrdersVerificationRule.MissingContext(nameof(SearchKnowledgeQuery));
        }

        var checks = new List<VerificationCheck>
        {
            new("Query", dto.Query, query.Query, dto.Query == query.Query),
            new("TopK", dto.Hits.Count.ToString(), $"<= {query.TopK}", dto.Hits.Count <= query.TopK)
        };

        foreach (Application.Common.Interfaces.KnowledgeHit hit in dto.Hits)
        {
            var row = await _queries.GetKnowledgeChunkAsync(hit.ChunkId, cancellationToken);
            bool match = row is not null
                && row.Text == hit.Text
                && row.DocumentTitle == hit.DocumentTitle
                && row.Section == hit.Section;
            checks.Add(new VerificationCheck(
                $"Chunk[{hit.ChunkId}]",
                $"{hit.DocumentTitle}§{hit.Section}",
                row is null ? "<不存在>" : $"{row.DocumentTitle}§{row.Section}",
                match));
        }

        return VerificationResult.FromChecks(checks);
    }
}

public class OeeVerificationRule : IVerificationRule
{
    private const double MinuteTolerance = 0.01;
    private const double RatioTolerance = 0.0001;

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

        EquipmentProfile? profile = await _queries.GetEquipmentProfileAsync(
            query.EquipmentId, query.EquipmentCode, cancellationToken);
        if (profile is null)
        {
            return new VerificationResult
            {
                Status = VerificationStatus.Discrepancy,
                Summary = "Equipment in the tool request does not resolve via the independent query path."
            };
        }

        ShiftCalendar calendar = await _calendarProvider.GetCalendarAsync(cancellationToken);
        DateOnly expectedDate = query.ProductionDate ?? calendar.GetProductionDate(asOf);
        IReadOnlyList<ShiftPeriod> periods = calendar.GetShiftPeriods(expectedDate);
        double plannedMinutes = periods.Sum(period => (period.EndUtc - period.StartUtc).TotalMinutes);
        DateTimeOffset dayStart = periods.Min(period => period.StartUtc);
        DateTimeOffset dayEnd = periods.Max(period => period.EndUtc);

        double actualDowntime = await _queries.SumDowntimeUnionMinutesAsync(
            profile.Id,
            periods.Select(period => (period.StartUtc, period.EndUtc)).ToList(),
            asOf,
            cancellationToken);

        double expectedRunning = Math.Max(0d, plannedMinutes - actualDowntime);
        double expectedAvailability = plannedMinutes > 0 ? expectedRunning / plannedMinutes : 0d;

        // 派生分量按同一公式独立重算(债 #8:Performance/Quality/Oee 不再只靠 DTO 自证)。
        double? expectedPerformance = null;
        double? expectedQuality = null;
        if (profile.ProductionLineId is int lineId)
        {
            LineProductionTotals totals = await _queries.GetLineProductionTotalsAsync(
                lineId, dayStart, dayEnd, cancellationToken);
            if (totals.Completed > 0)
            {
                expectedQuality = (double)(totals.Qualified / totals.Completed);
                if (profile.IdealCycleTimeSeconds > 0 && expectedRunning > 0)
                {
                    expectedPerformance = (double)totals.Completed * (double)profile.IdealCycleTimeSeconds / 60d
                        / expectedRunning;
                }
            }
        }

        double? expectedOee = expectedPerformance is not null && expectedQuality is not null
            ? expectedAvailability * expectedPerformance.Value * expectedQuality.Value
            : null;

        static bool NullableClose(double? claimed, double? expected) =>
            claimed is null ? expected is null
            : expected is not null && Math.Abs(claimed.Value - expected.Value) <= RatioTolerance;

        return VerificationResult.FromChecks(new[]
        {
            new VerificationCheck("EquipmentId", dto.EquipmentId.ToString(), profile.Id.ToString(), dto.EquipmentId == profile.Id),
            new VerificationCheck("EquipmentCode", dto.EquipmentCode, profile.Code, dto.EquipmentCode == profile.Code),
            new VerificationCheck("ProductionDate", dto.ProductionDate.ToString("O"), expectedDate.ToString("O"), dto.ProductionDate == expectedDate),
            new VerificationCheck("AsOfUtc", dto.AsOfUtc.ToString("O"), asOf.ToString("O"), dto.AsOfUtc == asOf),
            new VerificationCheck("PlannedMinutes", dto.PlannedMinutes.ToString("0.##"), plannedMinutes.ToString("0.##"), Math.Abs(plannedMinutes - dto.PlannedMinutes) <= MinuteTolerance),
            new VerificationCheck("DowntimeMinutes", dto.DowntimeMinutes.ToString("0.##"), actualDowntime.ToString("0.##"), Math.Abs(actualDowntime - dto.DowntimeMinutes) <= MinuteTolerance),
            new VerificationCheck("RunningMinutes", dto.RunningMinutes.ToString("0.##"), expectedRunning.ToString("0.##"), Math.Abs(expectedRunning - dto.RunningMinutes) <= MinuteTolerance),
            new VerificationCheck("Availability", dto.Availability.ToString("0.####"), expectedAvailability.ToString("0.####"), Math.Abs(dto.Availability - expectedAvailability) <= RatioTolerance),
            new VerificationCheck("Performance", dto.Performance?.ToString("0.####") ?? "null", expectedPerformance?.ToString("0.####") ?? "null", NullableClose(dto.Performance, expectedPerformance)),
            new VerificationCheck("Quality", dto.Quality?.ToString("0.####") ?? "null", expectedQuality?.ToString("0.####") ?? "null", NullableClose(dto.Quality, expectedQuality)),
            new VerificationCheck("Oee", dto.Oee?.ToString("0.####") ?? "null", expectedOee?.ToString("0.####") ?? "null", NullableClose(dto.Oee, expectedOee)),
            new VerificationCheck("Attribution", dto.Attribution, "line-level", dto.Attribution == "line-level")
        });
    }
}
