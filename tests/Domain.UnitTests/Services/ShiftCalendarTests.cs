using Lingban.Domain.Entities.Calendar;
using Lingban.Domain.Services;
using NUnit.Framework;
using Shouldly;

namespace Lingban.Domain.UnitTests.Services;

public class ShiftCalendarTests
{
    private static readonly TimeZoneInfo Shanghai = TimeZoneInfo.FindSystemTimeZoneById("Asia/Shanghai");

    private static ShiftCalendar TwoShiftCalendar() => new(
        Shanghai,
        new[]
        {
            new Shift { Code = "DAY", Name = "白班", StartLocalTime = new TimeOnly(8, 0), EndLocalTime = new TimeOnly(20, 0) },
            new Shift { Code = "NIGHT", Name = "夜班", StartLocalTime = new TimeOnly(20, 0), EndLocalTime = new TimeOnly(8, 0) }
        });

    private static DateTimeOffset ShanghaiLocal(int y, int mo, int d, int h, int mi) =>
        new(new DateTime(y, mo, d, h, mi, 0), TimeSpan.FromHours(8));

    [Test]
    public void BeforeEightBelongsToPreviousProductionDay()
    {
        // 验收:东八区 07:59 属于前一天的夜班,08:01 属于当天白班。
        var calendar = TwoShiftCalendar();

        ShiftPeriod? at0759 = calendar.Resolve(ShanghaiLocal(2026, 7, 22, 7, 59));
        at0759.ShouldNotBeNull();
        at0759.ShiftCode.ShouldBe("NIGHT");
        at0759.ProductionDate.ShouldBe(new DateOnly(2026, 7, 21));

        ShiftPeriod? at0801 = calendar.Resolve(ShanghaiLocal(2026, 7, 22, 8, 1));
        at0801.ShouldNotBeNull();
        at0801.ShiftCode.ShouldBe("DAY");
        at0801.ProductionDate.ShouldBe(new DateOnly(2026, 7, 22));
    }

    [Test]
    public void UtcMidnightDoesNotSplitTheProductionDay()
    {
        // 旧项目教训:UTC 划天会把东八区 08:00 前的产量全算昨天。
        // UTC 2026-07-21 23:00 = 上海 2026-07-22 07:00,必须归属 7-21 的夜班。
        var calendar = TwoShiftCalendar();

        var utcLateNight = new DateTimeOffset(2026, 7, 21, 23, 0, 0, TimeSpan.Zero);
        calendar.GetProductionDate(utcLateNight).ShouldBe(new DateOnly(2026, 7, 21));
    }

    [Test]
    public void NightShiftWindowSpansMidnight()
    {
        var calendar = TwoShiftCalendar();

        ShiftPeriod? night = calendar.Resolve(ShanghaiLocal(2026, 7, 22, 2, 30));
        night.ShouldNotBeNull();
        night.ShiftCode.ShouldBe("NIGHT");
        night.ProductionDate.ShouldBe(new DateOnly(2026, 7, 21));
        night.StartUtc.ShouldBe(ShanghaiLocal(2026, 7, 21, 20, 0).ToUniversalTime());
        night.EndUtc.ShouldBe(ShanghaiLocal(2026, 7, 22, 8, 0).ToUniversalTime());
    }

    [Test]
    public void ProductionDayBoundsCoverBothShifts()
    {
        var calendar = TwoShiftCalendar();

        var (startUtc, endUtc) = calendar.GetProductionDayBoundsUtc(new DateOnly(2026, 7, 22));

        startUtc.ShouldBe(ShanghaiLocal(2026, 7, 22, 8, 0).ToUniversalTime());
        endUtc.ShouldBe(ShanghaiLocal(2026, 7, 23, 8, 0).ToUniversalTime());
    }

    [Test]
    public void GapFallsBackToLocalCalendarDay()
    {
        var dayOnly = new ShiftCalendar(
            Shanghai,
            new[] { new Shift { Code = "DAY", StartLocalTime = new TimeOnly(8, 0), EndLocalTime = new TimeOnly(17, 0) } });

        dayOnly.Resolve(ShanghaiLocal(2026, 7, 22, 22, 0)).ShouldBeNull();
        dayOnly.GetProductionDate(ShanghaiLocal(2026, 7, 22, 22, 0)).ShouldBe(new DateOnly(2026, 7, 22));
    }

    [Test]
    public void OverlappingShiftsAreRejected()
    {
        // Codex 审查发现#9 的回归钉:重叠班次让时刻归属取决于集合顺序,构造时拒绝。
        Should.Throw<ArgumentException>(() => new ShiftCalendar(
            Shanghai,
            new[]
            {
                new Shift { Code = "A", StartLocalTime = new TimeOnly(8, 0), EndLocalTime = new TimeOnly(16, 0) },
                new Shift { Code = "B", StartLocalTime = new TimeOnly(12, 0), EndLocalTime = new TimeOnly(20, 0) }
            }));

        // 夜班尾部越过次日白班头部同样是重叠。
        Should.Throw<ArgumentException>(() => new ShiftCalendar(
            Shanghai,
            new[]
            {
                new Shift { Code = "DAY", StartLocalTime = new TimeOnly(7, 0), EndLocalTime = new TimeOnly(19, 0) },
                new Shift { Code = "NIGHT", StartLocalTime = new TimeOnly(20, 0), EndLocalTime = new TimeOnly(8, 0) }
            }));

        // 首尾相接(20:00 交班)是合法的。
        Should.NotThrow(() => TwoShiftCalendar());
    }

    [Test]
    public void RequiresAtLeastOneActiveShift()
    {
        Should.Throw<ArgumentException>(() => new ShiftCalendar(Shanghai, Array.Empty<Shift>()));
        Should.Throw<ArgumentException>(() => new ShiftCalendar(
            Shanghai,
            new[] { new Shift { Code = "OFF", IsActive = false, StartLocalTime = new TimeOnly(8, 0), EndLocalTime = new TimeOnly(20, 0) } }));
    }
}
