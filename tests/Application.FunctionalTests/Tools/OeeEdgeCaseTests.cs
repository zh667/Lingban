using Lingban.Application.Common;
using Lingban.Application.Common.Verification;
using Lingban.Application.Equipment.Queries;
using Lingban.Domain.Entities.Calendar;
using Lingban.Domain.Entities.Equipment;
using Lingban.Domain.Entities.Production;
using Lingban.Domain.Enums;
using Microsoft.Extensions.DependencyInjection;

namespace Lingban.Application.FunctionalTests.Tools;

/// <summary>
/// Codex 二审 #4/#5/#13 的回归钉:空档日历、重叠停机并集、开放停机 AsOf 截断。
/// 日历:08:00–12:00 与 13:00–17:00(上海),午休一小时不是计划时间。
/// 生产日 2026-07-21:上午班 UTC 00:00–04:00,下午班 UTC 05:00–09:00。
/// </summary>
public class OeeEdgeCaseTests : TestBase
{
    private static readonly DateTimeOffset PinnedAsOf = new(2026, 7, 21, 12, 0, 0, TimeSpan.Zero);

    private int _equipmentId;

    private async Task SeedGapCalendarFactoryAsync()
    {
        await TestApp.ExecuteDbContextAsync(async context =>
        {
            context.AddRange(
                new Shift { Code = "AM", Name = "上午班", StartLocalTime = new TimeOnly(8, 0), EndLocalTime = new TimeOnly(12, 0) },
                new Shift { Code = "PM", Name = "下午班", StartLocalTime = new TimeOnly(13, 0), EndLocalTime = new TimeOnly(17, 0) });

            var line = new ProductionLine { Code = "L7", Name = "七线" };
            var station = new Workstation { Code = "S7", Name = "工位七", ProductionLine = line };
            var equipment = new Domain.Entities.Equipment.Equipment
            {
                Code = "EQ-GAP",
                Name = "空档测试机",
                Workstation = station,
                IdealCycleTimeSeconds = 30m
            };
            context.AddRange(line, station, equipment);
            await context.SaveChangesAsync();
            _equipmentId = equipment.Id;
        });
    }

    [Test]
    public async Task GapBetweenShiftsIsNotPlannedTimeAndGapDowntimeIsIgnored()
    {
        await SeedGapCalendarFactoryAsync();
        await TestApp.ExecuteDbContextAsync(async context =>
        {
            // 停机完全落在午休空档(UTC 04:15–04:45):不得计入。
            context.Add(new DowntimeRecord
            {
                EquipmentId = _equipmentId,
                Reason = "午休空档停机",
                StartUtc = new DateTimeOffset(2026, 7, 21, 4, 15, 0, TimeSpan.Zero),
                EndUtc = new DateTimeOffset(2026, 7, 21, 4, 45, 0, TimeSpan.Zero),
                Source = DataSource.Simulated
            });
            await context.SaveChangesAsync();
        });

        var query = new CalculateOeeQuery(_equipmentId, new DateOnly(2026, 7, 21), PinnedAsOf);
        OeeDto oee = await TestApp.SendAsync(query);

        // 旧的首尾包络算法会得到 540(含午休);区间求和必须是 480。
        oee.PlannedMinutes.ShouldBe(480d, tolerance: 0.01);
        oee.DowntimeMinutes.ShouldBe(0d, tolerance: 0.01);
        oee.Availability.ShouldBe(1d, tolerance: 0.0001);

        (await VerifyAsync(query, oee)).Status.ShouldBe(VerificationStatus.Verified);
    }

    [Test]
    public async Task OverlappingDowntimesAreUnionedNotDoubleCounted()
    {
        await SeedGapCalendarFactoryAsync();
        await TestApp.ExecuteDbContextAsync(async context =>
        {
            // UTC 00:00–02:00 与 01:00–03:00:并集 180 分钟,双计会得到 240。
            context.AddRange(
                new DowntimeRecord
                {
                    EquipmentId = _equipmentId,
                    Reason = "故障A",
                    StartUtc = new DateTimeOffset(2026, 7, 21, 0, 0, 0, TimeSpan.Zero),
                    EndUtc = new DateTimeOffset(2026, 7, 21, 2, 0, 0, TimeSpan.Zero),
                    Source = DataSource.Simulated
                },
                new DowntimeRecord
                {
                    EquipmentId = _equipmentId,
                    Reason = "故障B",
                    StartUtc = new DateTimeOffset(2026, 7, 21, 1, 0, 0, TimeSpan.Zero),
                    EndUtc = new DateTimeOffset(2026, 7, 21, 3, 0, 0, TimeSpan.Zero),
                    Source = DataSource.Simulated
                });
            await context.SaveChangesAsync();
        });

        var query = new CalculateOeeQuery(_equipmentId, new DateOnly(2026, 7, 21), PinnedAsOf);
        OeeDto oee = await TestApp.SendAsync(query);

        oee.DowntimeMinutes.ShouldBe(180d, tolerance: 0.01);
        (await VerifyAsync(query, oee)).Status.ShouldBe(VerificationStatus.Verified);
    }

    [Test]
    public async Task OpenDowntimeIsClippedAtAsOfNotEndOfDay()
    {
        await SeedGapCalendarFactoryAsync();
        await TestApp.ExecuteDbContextAsync(async context =>
        {
            // 停机 UTC 01:00 开始,EndUtc 为 null;AsOf 02:00:只算 60 分钟,不许把未来 7 小时算成停机。
            context.Add(new DowntimeRecord
            {
                EquipmentId = _equipmentId,
                Reason = "进行中的停机",
                StartUtc = new DateTimeOffset(2026, 7, 21, 1, 0, 0, TimeSpan.Zero),
                Source = DataSource.Simulated
            });
            await context.SaveChangesAsync();
        });

        var asOf = new DateTimeOffset(2026, 7, 21, 2, 0, 0, TimeSpan.Zero);
        var query = new CalculateOeeQuery(_equipmentId, new DateOnly(2026, 7, 21), asOf);
        OeeDto oee = await TestApp.SendAsync(query);

        oee.DowntimeMinutes.ShouldBe(60d, tolerance: 0.01);
        (await VerifyAsync(query, oee)).Status.ShouldBe(VerificationStatus.Verified);
    }

    [Test]
    public async Task ClosedDowntimeIsAlsoClippedForHistoricalReplay()
    {
        // Codex 三审 #4:已关闭记录(01:00–03:00)在 as-of 02:00 的回放里只能看到 60 分钟。
        await SeedGapCalendarFactoryAsync();
        await TestApp.ExecuteDbContextAsync(async context =>
        {
            context.Add(new DowntimeRecord
            {
                EquipmentId = _equipmentId,
                Reason = "已结束的停机",
                StartUtc = new DateTimeOffset(2026, 7, 21, 1, 0, 0, TimeSpan.Zero),
                EndUtc = new DateTimeOffset(2026, 7, 21, 3, 0, 0, TimeSpan.Zero),
                Source = DataSource.Simulated
            });
            await context.SaveChangesAsync();
        });

        var replayAsOf = new DateTimeOffset(2026, 7, 21, 2, 0, 0, TimeSpan.Zero);
        var query = new CalculateOeeQuery(_equipmentId, new DateOnly(2026, 7, 21), replayAsOf);
        OeeDto oee = await TestApp.SendAsync(query);

        oee.DowntimeMinutes.ShouldBe(60d, tolerance: 0.01);
        (await VerifyAsync(query, oee)).Status.ShouldBe(VerificationStatus.Verified);
    }

    [Test]
    public async Task TamperedOeeDowntimeIsCaught()
    {
        await SeedGapCalendarFactoryAsync();
        var query = new CalculateOeeQuery(_equipmentId, new DateOnly(2026, 7, 21), PinnedAsOf);
        OeeDto oee = await TestApp.SendAsync(query);

        OeeDto tampered = oee with { DowntimeMinutes = oee.DowntimeMinutes + 30d };
        (await VerifyAsync(query, tampered)).Status.ShouldBe(VerificationStatus.Discrepancy);
    }

    private static async Task<VerificationResult> VerifyAsync(CalculateOeeQuery query, OeeDto result)
    {
        using var scope = FunctionalTestSetup.ScopeFactory.CreateScope();
        var verifier = scope.ServiceProvider.GetRequiredService<IFactVerifier>();
        return await verifier.VerifyAsync(ToolNames.CalculateOee, query, result);
    }
}
