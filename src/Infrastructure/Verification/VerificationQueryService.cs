using Lingban.Application.Common.Interfaces;
using Lingban.Application.Common.Verification;
using Lingban.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Lingban.Infrastructure.Verification;

/// <summary>
/// 校验专用查询:原生 SQL,与工具的 LINQ 管道零共享(Agent 铁律 #1)。
/// 原生 SQL 不经过全局查询过滤器,租户条件必须显式写进 WHERE。
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

    public async Task<int> CountWorkOrdersStartedBetweenAsync(
        DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken cancellationToken)
    {
        string tenant = _tenantContext.TenantId;
        return await _context.Database.SqlQuery<int>($"""
            SELECT COUNT(*)::int AS "Value"
            FROM "WorkOrders"
            WHERE "TenantId" = {tenant}
              AND (
                    ("ActualStartUtc" >= {fromUtc} AND "ActualStartUtc" < {toUtc})
                 OR ("ActualStartUtc" IS NULL AND "PlannedStartUtc" >= {fromUtc} AND "PlannedStartUtc" < {toUtc})
              )
            """).SingleAsync(cancellationToken);
    }

    public async Task<int> CountDelayedWorkOrdersAsync(DateTimeOffset asOfUtc, CancellationToken cancellationToken)
    {
        string tenant = _tenantContext.TenantId;
        return await _context.Database.SqlQuery<int>($"""
            SELECT COUNT(*)::int AS "Value"
            FROM "WorkOrders"
            WHERE "TenantId" = {tenant}
              AND "Status" NOT IN (3, 4)
              AND "PlannedEndUtc" IS NOT NULL
              AND "PlannedEndUtc" < {asOfUtc}
            """).SingleAsync(cancellationToken);
    }

    public async Task<decimal> SumDefectQuantitySinceAsync(DateTimeOffset sinceUtc, CancellationToken cancellationToken)
    {
        string tenant = _tenantContext.TenantId;
        return await _context.Database.SqlQuery<decimal>($"""
            SELECT COALESCE(SUM("Quantity"), 0) AS "Value"
            FROM "DefectRecords"
            WHERE "TenantId" = {tenant}
              AND "RecordedAtUtc" >= {sinceUtc}
            """).SingleAsync(cancellationToken);
    }

    public async Task<double> SumDowntimeMinutesAsync(
        int equipmentId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken cancellationToken)
    {
        string tenant = _tenantContext.TenantId;
        return await _context.Database.SqlQuery<double>($"""
            SELECT COALESCE(SUM(
                EXTRACT(EPOCH FROM (
                    LEAST(COALESCE("EndUtc", {toUtc}), {toUtc})
                    - GREATEST("StartUtc", {fromUtc})
                )) / 60.0), 0)::float8 AS "Value"
            FROM "DowntimeRecords"
            WHERE "TenantId" = {tenant}
              AND "EquipmentId" = {equipmentId}
              AND "StartUtc" < {toUtc}
              AND COALESCE("EndUtc", {toUtc}) > {fromUtc}
            """).SingleAsync(cancellationToken);
    }
}
