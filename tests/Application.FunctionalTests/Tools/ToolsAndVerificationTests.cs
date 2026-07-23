using Lingban.Application.Common;
using Lingban.Application.Common.Interfaces;
using Lingban.Application.Common.Verification;
using Lingban.Application.Equipment.Queries;
using Lingban.Application.Production.Queries;
using Lingban.Application.Quality.Queries;
using Lingban.Domain.Entities.Calendar;
using Lingban.Domain.Entities.Equipment;
using Lingban.Domain.Entities.Materials;
using Lingban.Domain.Entities.Production;
using Lingban.Domain.Entities.Quality;
using Lingban.Domain.Enums;
using Lingban.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Lingban.Application.FunctionalTests.Tools;

/// <summary>
/// M2 工具查询 + FactVerifier:生产日按班次切分(旧项目 UTC 划天病根的端到端验证)、
/// OEE 计划时间用班次区间(债 #9)、校验规则走独立 SQL 路径。
/// 上海时区:生产日 2026-07-21 = UTC 07-21 00:00 至 07-22 00:00(双班无空档)。
/// </summary>
public class ToolsAndVerificationTests : TestBase
{
    private static readonly DateTimeOffset DayShiftUtc = new(2026, 7, 21, 2, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset NightShiftLateUtc = new(2026, 7, 21, 23, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset NextDayUtc = new(2026, 7, 22, 1, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset AsOf = new(2026, 7, 21, 13, 0, 0, TimeSpan.Zero);

    private int _lineId;
    private int _equipmentId;

    private async Task SeedFactoryAsync()
    {
        await TestApp.ExecuteDbContextAsync(async context =>
        {
            context.AddRange(
                new Shift { Code = "DAY", Name = "白班", StartLocalTime = new TimeOnly(8, 0), EndLocalTime = new TimeOnly(20, 0) },
                new Shift { Code = "NIGHT", Name = "夜班", StartLocalTime = new TimeOnly(20, 0), EndLocalTime = new TimeOnly(8, 0) });

            var product = new Product { Code = "FG-T", Name = "测试成品" };
            var line = new ProductionLine { Code = "L3", Name = "三线" };
            var station = new Workstation { Code = "S3", Name = "工位三", ProductionLine = line };
            var equipment = new Domain.Entities.Equipment.Equipment
            {
                Code = "EQ-1",
                Name = "贴片机一号",
                Workstation = station,
                IdealCycleTimeSeconds = 30m
            };
            context.AddRange(product, line, station, equipment);
            await context.SaveChangesAsync();

            // A:白班开工;B:7-21 夜班尾段(上海 7-22 早 07:00)——必须算 7-21;C:次日白班,不算。
            var orderA = WorkOrder.Create("WO-DAY", product.Id, line.Id, 100m, "PCS");
            orderA.Release();
            orderA.Start(DayShiftUtc);
            orderA.ReportProduction(100m, 90m, 6m, 4m);

            var orderB = WorkOrder.Create("WO-NIGHT", product.Id, line.Id, 50m, "PCS");
            orderB.Release();
            orderB.Start(NightShiftLateUtc);

            var orderC = WorkOrder.Create("WO-NEXT", product.Id, line.Id, 30m, "PCS");
            orderC.Release();
            orderC.Start(NextDayUtc);

            // 延期:计划结束已过、仍未开工。
            var orderLate = WorkOrder.Create("WO-LATE", product.Id, line.Id, 10m, "PCS");
            orderLate.PlannedStartUtc = AsOf.AddDays(-3);
            orderLate.PlannedEndUtc = AsOf.AddDays(-1);
            orderLate.Release();

            context.AddRange(orderA, orderB, orderC, orderLate);

            // 停机:白班内 60 分钟 + 跨生产日边界的 60 分钟(只有前 30 分钟落在 7-21)。
            context.AddRange(
                new DowntimeRecord
                {
                    Equipment = equipment,
                    Reason = "换料",
                    StartUtc = new DateTimeOffset(2026, 7, 21, 1, 0, 0, TimeSpan.Zero),
                    EndUtc = new DateTimeOffset(2026, 7, 21, 2, 0, 0, TimeSpan.Zero),
                    Source = DataSource.Simulated
                },
                new DowntimeRecord
                {
                    Equipment = equipment,
                    Reason = "故障",
                    StartUtc = new DateTimeOffset(2026, 7, 21, 23, 30, 0, TimeSpan.Zero),
                    EndUtc = new DateTimeOffset(2026, 7, 22, 0, 30, 0, TimeSpan.Zero),
                    Source = DataSource.Simulated
                });

            var defectType = new DefectType { Code = "SOLDER", Name = "连锡" };
            context.AddRange(
                new DefectRecord { DefectType = defectType, WorkOrder = orderA, Quantity = 6m, RecordedAtUtc = DayShiftUtc.AddHours(3) },
                new DefectRecord { DefectType = defectType, WorkOrder = orderA, Quantity = 4m, RecordedAtUtc = DayShiftUtc.AddHours(4) });

            await context.SaveChangesAsync();

            _lineId = line.Id;
            _equipmentId = equipment.Id;
        });
    }

    private static async Task<VerificationResult> VerifyAsync(string toolName, object request, object result)
    {
        using var scope = FunctionalTestSetup.ScopeFactory.CreateScope();
        var verifier = scope.ServiceProvider.GetRequiredService<IFactVerifier>();
        return await verifier.VerifyAsync(toolName, request, result);
    }

    [Test]
    public async Task TodayWorkOrdersFollowShiftCalendarNotUtcDate()
    {
        await SeedFactoryAsync();

        var todayQuery = new GetTodayWorkOrdersQuery(AsOfUtc: AsOf);
        TodayWorkOrdersDto today = await TestApp.SendAsync(todayQuery);

        today.ProductionDate.ShouldBe(new DateOnly(2026, 7, 21));
        today.Orders.Select(order => order.Code)
            .ShouldBe(new[] { "WO-DAY", "WO-NIGHT" }, ignoreOrder: true);
        today.TotalCount.ShouldBe(2);
        today.InProgressCount.ShouldBe(2);

        VerificationResult verification = await VerifyAsync(ToolNames.GetTodayWorkOrders, todayQuery, today);
        verification.Status.ShouldBe(VerificationStatus.Verified);
    }

    [Test]
    public async Task TamperedToolResultIsCaughtByIndependentQueryPath()
    {
        await SeedFactoryAsync();

        var todayQuery = new GetTodayWorkOrdersQuery(AsOfUtc: AsOf);
        TodayWorkOrdersDto today = await TestApp.SendAsync(todayQuery);
        TodayWorkOrdersDto tampered = today with { TotalCount = today.TotalCount + 5 };

        VerificationResult verification = await VerifyAsync(ToolNames.GetTodayWorkOrders, todayQuery, tampered);
        verification.Status.ShouldBe(VerificationStatus.Discrepancy);
        verification.Checks.ShouldContain(check => !check.Match);
    }

    [Test]
    public async Task DelayedOrdersAreFoundAndVerified()
    {
        await SeedFactoryAsync();

        var delayedQuery = new AnalyzeDelayedOrdersQuery(AsOf);
        DelayedOrdersDto delayed = await TestApp.SendAsync(delayedQuery);

        delayed.TotalCount.ShouldBe(1);
        delayed.Orders[0].Code.ShouldBe("WO-LATE");
        delayed.Orders[0].DelayHours.ShouldBe(24d, tolerance: 0.01);

        (await VerifyAsync(ToolNames.AnalyzeDelayedOrders, delayedQuery, delayed)).Status.ShouldBe(VerificationStatus.Verified);
    }

    [Test]
    public async Task DefectSummaryAggregatesAndVerifies()
    {
        await SeedFactoryAsync();

        var summaryQuery = new GetDefectSummaryQuery(7, AsOf.AddDays(1));
        DefectSummaryDto summary = await TestApp.SendAsync(summaryQuery);

        summary.TotalQuantity.ShouldBe(10m);
        summary.ByType.Count.ShouldBe(1);
        summary.ByType[0].Code.ShouldBe("SOLDER");
        summary.ByType[0].Share.ShouldBe(1d);

        (await VerifyAsync(ToolNames.GetDefectSummary, summaryQuery, summary)).Status.ShouldBe(VerificationStatus.Verified);
    }

    [Test]
    public async Task OeePlannedTimeUsesShiftPeriodsAndDowntimeOverlap()
    {
        await SeedFactoryAsync();

        var oeeQuery = new CalculateOeeQuery(_equipmentId, new DateOnly(2026, 7, 21), new DateTimeOffset(2026, 7, 22, 0, 0, 0, TimeSpan.Zero));
        OeeDto oee = await TestApp.SendAsync(oeeQuery);

        oee.PlannedMinutes.ShouldBe(1440d, tolerance: 0.01);
        // 60 分钟白班停机 + 跨边界停机只计入 7-21 内的 30 分钟。
        oee.DowntimeMinutes.ShouldBe(90d, tolerance: 0.01);
        oee.Availability.ShouldBe(1350d / 1440d, tolerance: 0.0001);
        oee.Quality.ShouldNotBeNull();
        oee.Quality!.Value.ShouldBe(0.9d, tolerance: 0.0001);
        oee.Performance.ShouldNotBeNull();
        oee.Oee.ShouldNotBeNull();
        oee.Attribution.ShouldBe("line-level");

        (await VerifyAsync(ToolNames.CalculateOee, oeeQuery, oee)).Status.ShouldBe(VerificationStatus.Verified);
    }

    [Test]
    public async Task LineFilteredTodayQueryVerifiesCorrectly()
    {
        // Codex 二审 #6:产线过滤必须进入校验路径,正确结果不得被误判 Discrepancy。
        await SeedFactoryAsync();

        var filteredQuery = new GetTodayWorkOrdersQuery(_lineId, AsOf);
        TodayWorkOrdersDto filtered = await TestApp.SendAsync(filteredQuery);

        filtered.TotalCount.ShouldBe(2);
        (await VerifyAsync(ToolNames.GetTodayWorkOrders, filteredQuery, filtered)).Status.ShouldBe(VerificationStatus.Verified);
    }

    [Test]
    public async Task DefectVerificationRespectsAsOfUpperBound()
    {
        // Codex 二审 #7:历史回放时,晚于 AsOf 的新记录不得污染校验。
        await SeedFactoryAsync();
        DateTimeOffset historicalAsOf = AsOf.AddDays(1);

        var replayQuery = new GetDefectSummaryQuery(7, historicalAsOf);
        DefectSummaryDto summary = await TestApp.SendAsync(replayQuery);
        summary.TotalQuantity.ShouldBe(10m);

        await TestApp.ExecuteDbContextAsync(async context =>
        {
            var lateType = new Domain.Entities.Quality.DefectType { Code = "LATE", Name = "晚到缺陷" };
            context.Add(new Domain.Entities.Quality.DefectRecord
            {
                DefectType = lateType,
                Quantity = 99m,
                RecordedAtUtc = historicalAsOf.AddHours(1)
            });
            await context.SaveChangesAsync();
        });

        (await VerifyAsync(ToolNames.GetDefectSummary, replayQuery, summary)).Status.ShouldBe(VerificationStatus.Verified);
    }

    [Test]
    public async Task TamperedSecondaryFactsAreCaught()
    {
        // Codex 二审 #8:不止总数——状态分布、明细 ID、分类合计都要被独立路径钉住。
        await SeedFactoryAsync();

        var todayQuery = new GetTodayWorkOrdersQuery(AsOfUtc: AsOf);
        TodayWorkOrdersDto today = await TestApp.SendAsync(todayQuery);
        (await VerifyAsync(ToolNames.GetTodayWorkOrders, todayQuery, today with { InProgressCount = 99 }))
            .Status.ShouldBe(VerificationStatus.Discrepancy);
        (await VerifyAsync(ToolNames.GetTodayWorkOrders, todayQuery, today with { FromUtc = today.FromUtc.AddHours(-8) }))
            .Status.ShouldBe(VerificationStatus.Discrepancy);
        // 工具选错生产日但输出自洽边界:规则从请求重推生产日,必须抓到。
        (await VerifyAsync(ToolNames.GetTodayWorkOrders, todayQuery, today with { ProductionDate = today.ProductionDate.AddDays(-1) }))
            .Status.ShouldBe(VerificationStatus.Discrepancy);

        var delayedQuery = new AnalyzeDelayedOrdersQuery(AsOf);
        DelayedOrdersDto delayed = await TestApp.SendAsync(delayedQuery);
        DelayedOrdersDto delayedTampered = delayed with
        {
            Orders = delayed.Orders.Select(order => order with { Id = order.Id + 1000 }).ToList()
        };
        (await VerifyAsync(ToolNames.AnalyzeDelayedOrders, delayedQuery, delayedTampered))
            .Status.ShouldBe(VerificationStatus.Discrepancy);

        var summaryQuery = new GetDefectSummaryQuery(7, AsOf.AddDays(1));
        DefectSummaryDto summary = await TestApp.SendAsync(summaryQuery);
        (await VerifyAsync(ToolNames.GetDefectSummary, summaryQuery, summary with { TotalQuantity = summary.TotalQuantity + 1 }))
            .Status.ShouldBe(VerificationStatus.Discrepancy);
    }

    [Test]
    public async Task ExecutedSqlIsCapturedForDebugInfo()
    {
        using var scope = FunctionalTestSetup.ScopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var queryLog = scope.ServiceProvider.GetRequiredService<IQueryLog>();

        await context.WorkOrders.AsNoTracking().ToListAsync();

        queryLog.Statements.ShouldNotBeEmpty();
        queryLog.Statements.ShouldContain(statement => statement.Contains("SELECT") && statement.Contains("WorkOrders"));
    }
}
