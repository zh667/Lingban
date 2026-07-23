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

    public async Task<int?> ResolveProductionLineIdByCodeAsync(string code, CancellationToken cancellationToken)
    {
        string tenant = _tenantContext.TenantId;
        List<int> ids = await _context.Database.SqlQuery<int>($"""
            SELECT "Id" AS "Value" FROM "ProductionLines"
            WHERE "TenantId" = {tenant} AND "Code" = {code}
            """).ToListAsync(cancellationToken);
        return ids.Count == 1 ? ids[0] : null;
    }

    public async Task<TodayWorkOrderCounts> CountTodayWorkOrdersAsync(
        DateTimeOffset fromUtc, DateTimeOffset toUtc, int? productionLineId, CancellationToken cancellationToken)
    {
        string tenant = _tenantContext.TenantId;
        int lineFilter = productionLineId ?? -1;

        CountsRow row = await _context.Database.SqlQuery<CountsRow>($"""
            SELECT COUNT(*)::int AS "Total",
                   COUNT(*) FILTER (WHERE "Status" = 2)::int AS "InProgress",
                   COUNT(*) FILTER (WHERE "Status" = 3)::int AS "Completed",
                   COALESCE(SUM("PlannedQuantity"), 0) AS "PlannedSum",
                   COALESCE(SUM("CompletedQuantity"), 0) AS "CompletedSum",
                   COALESCE(SUM("QualifiedQuantity"), 0) AS "QualifiedSum",
                   COALESCE(SUM("ScrapQuantity"), 0) AS "ScrapSum"
            FROM "WorkOrders"
            WHERE "TenantId" = {tenant}
              AND ({lineFilter} = -1 OR "ProductionLineId" = {lineFilter})
              AND (
                    ("ActualStartUtc" >= {fromUtc} AND "ActualStartUtc" < {toUtc})
                 OR ("ActualStartUtc" IS NULL AND "PlannedStartUtc" >= {fromUtc} AND "PlannedStartUtc" < {toUtc})
              )
            """).SingleAsync(cancellationToken);

        return new TodayWorkOrderCounts(
            row.Total, row.InProgress, row.Completed,
            row.PlannedSum, row.CompletedSum, row.QualifiedSum, row.ScrapSum);
    }

    public async Task<IReadOnlyList<DelayedOrderRow>> GetDelayedWorkOrderRowsAsync(
        DateTimeOffset asOfUtc, int? productionLineId, CancellationToken cancellationToken)
    {
        string tenant = _tenantContext.TenantId;
        int lineFilter = productionLineId ?? -1;
        List<DelayedRow> rows = await _context.Database.SqlQuery<DelayedRow>($"""
            SELECT "Id", "PlannedEndUtc", "PlannedQuantity", "CompletedQuantity"
            FROM "WorkOrders"
            WHERE "TenantId" = {tenant}
              AND ({lineFilter} = -1 OR "ProductionLineId" = {lineFilter})
              AND "Status" NOT IN (3, 4)
              AND "PlannedEndUtc" IS NOT NULL
              AND "PlannedEndUtc" < {asOfUtc}
            ORDER BY "Id"
            """).ToListAsync(cancellationToken);

        return rows
            .Select(row => new DelayedOrderRow(row.Id, row.PlannedEndUtc!.Value, row.PlannedQuantity, row.CompletedQuantity))
            .ToList();
    }

    public async Task<IReadOnlyList<DefectTypeRow>> GetDefectTypeRowsAsync(
        DateTimeOffset sinceUtc, DateTimeOffset asOfUtc, CancellationToken cancellationToken)
    {
        string tenant = _tenantContext.TenantId;
        List<DefectRow> rows = await _context.Database.SqlQuery<DefectRow>($"""
            SELECT t."Code", COALESCE(SUM(r."Quantity"), 0) AS "Quantity"
            FROM "DefectRecords" r
            JOIN "DefectTypes" t ON t."Id" = r."DefectTypeId"
            WHERE r."TenantId" = {tenant}
              AND r."RecordedAtUtc" >= {sinceUtc}
              AND r."RecordedAtUtc" < {asOfUtc}
            GROUP BY t."Code"
            ORDER BY t."Code"
            """).ToListAsync(cancellationToken);
        return rows.Select(row => new DefectTypeRow(row.Code, row.Quantity)).ToList();
    }

    public async Task<EquipmentProfile?> GetEquipmentProfileAsync(
        int? equipmentId, string? equipmentCode, CancellationToken cancellationToken)
    {
        string tenant = _tenantContext.TenantId;
        int idFilter = equipmentId ?? -1;
        string codeFilter = equipmentCode ?? string.Empty;
        List<ProfileRow> rows = await _context.Database.SqlQuery<ProfileRow>($"""
            SELECT e."Id", e."Code", w."ProductionLineId", e."IdealCycleTimeSeconds"
            FROM "Equipment" e
            LEFT JOIN "Workstations" w ON w."Id" = e."WorkstationId"
            WHERE e."TenantId" = {tenant}
              AND (({idFilter} <> -1 AND e."Id" = {idFilter})
                OR ({idFilter} = -1 AND e."Code" = {codeFilter}))
            """).ToListAsync(cancellationToken);
        ProfileRow? row = rows.SingleOrDefault();
        return row is null
            ? null
            : new EquipmentProfile(row.Id, row.Code, row.ProductionLineId, row.IdealCycleTimeSeconds);
    }

    public async Task<LineProductionTotals> GetLineProductionTotalsAsync(
        int productionLineId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken cancellationToken)
    {
        string tenant = _tenantContext.TenantId;
        TotalsRow row = await _context.Database.SqlQuery<TotalsRow>($"""
            SELECT COALESCE(SUM("CompletedQuantity"), 0) AS "Completed",
                   COALESCE(SUM("QualifiedQuantity"), 0) AS "Qualified"
            FROM "WorkOrders"
            WHERE "TenantId" = {tenant}
              AND "ProductionLineId" = {productionLineId}
              AND "ActualStartUtc" >= {fromUtc}
              AND "ActualStartUtc" < {toUtc}
            """).SingleAsync(cancellationToken);
        return new LineProductionTotals(row.Completed, row.Qualified);
    }

    public async Task<KnowledgeChunkRow?> GetKnowledgeChunkAsync(int chunkId, CancellationToken cancellationToken)
    {
        string tenant = _tenantContext.TenantId;
        List<ChunkRow> rows = await _context.Database.SqlQuery<ChunkRow>($"""
            SELECT d."Title" AS "DocumentTitle", c."Section", c."Text", (c."Embedding" IS NOT NULL) AS "HasEmbedding"
            FROM "KnowledgeChunks" c
            JOIN "KnowledgeDocuments" d ON d."TenantId" = c."TenantId" AND d."Id" = c."DocumentId"
            WHERE c."TenantId" = {tenant} AND c."Id" = {chunkId}
            """).ToListAsync(cancellationToken);
        ChunkRow? row = rows.SingleOrDefault();
        return row is null ? null : new KnowledgeChunkRow(row.DocumentTitle, row.Section, row.Text, row.HasEmbedding);
    }

    public async Task<int> CountRetrievableChunksAsync(CancellationToken cancellationToken)
    {
        string tenant = _tenantContext.TenantId;
        return await _context.Database.SqlQuery<int>($"""
            SELECT COUNT(*)::int AS "Value" FROM "KnowledgeChunks"
            WHERE "TenantId" = {tenant} AND "Embedding" IS NOT NULL
            """).SingleAsync(cancellationToken);
    }

    public async Task<PendingActionRow?> GetPendingActionAsync(int actionId, CancellationToken cancellationToken)
    {
        string tenant = _tenantContext.TenantId;
        List<PendingActionRow> rows = await _context.Database.SqlQuery<PendingActionRow>($"""
            SELECT "OwnerUserId", "ActionType", "Status", "PayloadJson"
            FROM "PendingActions"
            WHERE "TenantId" = {tenant} AND "Id" = {actionId}
            """).ToListAsync(cancellationToken);
        return rows.SingleOrDefault();
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
            if (end > openClip)
            {
                end = openClip;
            }

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

    private sealed class ChunkRow
    {
        public string DocumentTitle { get; set; } = string.Empty;

        public string Section { get; set; } = string.Empty;

        public string Text { get; set; } = string.Empty;

        public bool HasEmbedding { get; set; }
    }

    private sealed class CountsRow
    {
        public int Total { get; set; }

        public int InProgress { get; set; }

        public int Completed { get; set; }

        public decimal PlannedSum { get; set; }

        public decimal CompletedSum { get; set; }

        public decimal QualifiedSum { get; set; }

        public decimal ScrapSum { get; set; }
    }

    private sealed class DelayedRow
    {
        public int Id { get; set; }

        public DateTimeOffset? PlannedEndUtc { get; set; }

        public decimal PlannedQuantity { get; set; }

        public decimal CompletedQuantity { get; set; }
    }

    private sealed class DefectRow
    {
        public string Code { get; set; } = string.Empty;

        public decimal Quantity { get; set; }
    }

    private sealed class ProfileRow
    {
        public int Id { get; set; }

        public string Code { get; set; } = string.Empty;

        public int? ProductionLineId { get; set; }

        public decimal IdealCycleTimeSeconds { get; set; }
    }

    private sealed class TotalsRow
    {
        public decimal Completed { get; set; }

        public decimal Qualified { get; set; }
    }

    private sealed class IntervalRow
    {
        public DateTimeOffset Start { get; set; }

        public DateTimeOffset? End { get; set; }
    }
}
