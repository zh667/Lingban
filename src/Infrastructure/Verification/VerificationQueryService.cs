using Lingban.Application.Common.Interfaces;
using Lingban.Application.Common.Verification;
using Lingban.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Lingban.Infrastructure.Verification;

/// <summary>
/// 校验专用查询:原生 SQL,与工具的 LINQ 管道零共享(Agent 铁律 #1)。
/// 原生 SQL 不经过全局查询过滤器,租户条件必须显式写进 WHERE。
/// 状态字面量:InProgress=2,Completed=3,Cancelled=4(与 WorkOrderStatus 枚举对齐)。
/// </summary>
public class VerificationQueryService : IVerificationQueryService
{
    private readonly ApplicationDbContext _context;
    private readonly ITenantContext _tenantContext;

    public VerificationQueryService(ApplicationDbContext context, ITenantContext tenantContext)
    {
        _context = context;
        _tenantContext = tenantContext;
    }

    public async Task<TodayWorkOrderCounts> CountTodayWorkOrdersAsync(
        DateTimeOffset fromUtc, DateTimeOffset toUtc, int? productionLineId, CancellationToken cancellationToken)
    {
        string tenant = _tenantContext.TenantId;
        // -1 哨兵表示"不过滤产线",避免拼接两套 SQL。
        int lineFilter = productionLineId ?? -1;

        CountsRow row = await _context.Database.SqlQuery<CountsRow>($"""
            SELECT COUNT(*)::int AS "Total",
                   COUNT(*) FILTER (WHERE "Status" = 2)::int AS "InProgress",
                   COUNT(*) FILTER (WHERE "Status" = 3)::int AS "Completed"
            FROM "WorkOrders"
            WHERE "TenantId" = {tenant}
              AND ({lineFilter} = -1 OR "ProductionLineId" = {lineFilter})
              AND (
                    ("ActualStartUtc" >= {fromUtc} AND "ActualStartUtc" < {toUtc})
                 OR ("ActualStartUtc" IS NULL AND "PlannedStartUtc" >= {fromUtc} AND "PlannedStartUtc" < {toUtc})
              )
            """).SingleAsync(cancellationToken);

        return new TodayWorkOrderCounts(row.Total, row.InProgress, row.Completed);
    }

    public async Task<IReadOnlyList<int>> GetDelayedWorkOrderIdsAsync(
        DateTimeOffset asOfUtc, CancellationToken cancellationToken)
    {
        string tenant = _tenantContext.TenantId;
        return await _context.Database.SqlQuery<int>($"""
            SELECT "Id" AS "Value"
            FROM "WorkOrders"
            WHERE "TenantId" = {tenant}
              AND "Status" NOT IN (3, 4)
              AND "PlannedEndUtc" IS NOT NULL
              AND "PlannedEndUtc" < {asOfUtc}
            ORDER BY "Id"
            """).ToListAsync(cancellationToken);
    }

    public async Task<decimal> SumDefectQuantityBetweenAsync(
        DateTimeOffset sinceUtc, DateTimeOffset asOfUtc, CancellationToken cancellationToken)
    {
        string tenant = _tenantContext.TenantId;
        return await _context.Database.SqlQuery<decimal>($"""
            SELECT COALESCE(SUM("Quantity"), 0) AS "Value"
            FROM "DefectRecords"
            WHERE "TenantId" = {tenant}
              AND "RecordedAtUtc" >= {sinceUtc}
              AND "RecordedAtUtc" < {asOfUtc}
            """).SingleAsync(cancellationToken);
    }

    public async Task<double> SumDowntimeUnionMinutesAsync(
        int equipmentId,
        IReadOnlyList<(DateTimeOffset FromUtc, DateTimeOffset ToUtc)> periods,
        DateTimeOffset clipUtc,
        CancellationToken cancellationToken)
    {
        if (periods.Count == 0)
        {
            return 0d;
        }

        string tenant = _tenantContext.TenantId;
        DateTimeOffset envelopeStart = periods.Min(period => period.FromUtc);
        DateTimeOffset envelopeEnd = periods.Max(period => period.ToUtc);

        List<IntervalRow> rows = await _context.Database.SqlQuery<IntervalRow>($"""
            SELECT "StartUtc" AS "Start", "EndUtc" AS "End"
            FROM "DowntimeRecords"
            WHERE "TenantId" = {tenant}
              AND "EquipmentId" = {equipmentId}
              AND "StartUtc" < {envelopeEnd}
              AND COALESCE("EndUtc", {envelopeEnd}) > {envelopeStart}
            ORDER BY "StartUtc"
            """).ToListAsync(cancellationToken);

        DateTimeOffset openClip = clipUtc < envelopeEnd ? clipUtc : envelopeEnd;
        var merged = new List<(DateTimeOffset Start, DateTimeOffset End)>();
        foreach (IntervalRow row in rows)
        {
            DateTimeOffset end = row.End ?? openClip;
            if (end <= row.Start)
            {
                continue;
            }

            if (merged.Count > 0 && row.Start <= merged[^1].End)
            {
                if (end > merged[^1].End)
                {
                    merged[^1] = (merged[^1].Start, end);
                }
            }
            else
            {
                merged.Add((row.Start, end));
            }
        }

        double minutes = 0d;
        foreach (var interval in merged)
        {
            foreach (var period in periods)
            {
                DateTimeOffset overlapStart = interval.Start > period.FromUtc ? interval.Start : period.FromUtc;
                DateTimeOffset overlapEnd = interval.End < period.ToUtc ? interval.End : period.ToUtc;
                if (overlapEnd > overlapStart)
                {
                    minutes += (overlapEnd - overlapStart).TotalMinutes;
                }
            }
        }

        return minutes;
    }

    private sealed class CountsRow
    {
        public int Total { get; set; }

        public int InProgress { get; set; }

        public int Completed { get; set; }
    }

    private sealed class IntervalRow
    {
        public DateTimeOffset Start { get; set; }

        public DateTimeOffset? End { get; set; }
    }
}
