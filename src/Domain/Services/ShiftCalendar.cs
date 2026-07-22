using Lingban.Domain.Entities.Calendar;

namespace Lingban.Domain.Services;

/// <summary>
/// 某一 UTC 时刻落入的班次窗口:生产日按"开班日"归属(跨天夜班属于其开始的那天)。
/// </summary>
public sealed record ShiftPeriod(
    string ShiftCode,
    DateOnly ProductionDate,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc);

/// <summary>
/// 工厂班次日历。领域铁律:存储一律 UTC,"今天/本班次"按工厂时区与班次切分,
/// 全库禁止用 DateTime.UtcNow.Date 划天(由 BannedTimeApisTests 机械保证)。
/// </summary>
public class ShiftCalendar
{
    private readonly TimeZoneInfo _factoryTimeZone;
    private readonly IReadOnlyList<Shift> _shifts;

    public ShiftCalendar(TimeZoneInfo factoryTimeZone, IEnumerable<Shift> shifts)
    {
        _factoryTimeZone = factoryTimeZone;
        _shifts = shifts.Where(shift => shift.IsActive).ToList();

        if (_shifts.Count == 0)
        {
            throw new ArgumentException("At least one active shift is required.", nameof(shifts));
        }

        RejectOverlaps(_shifts);
    }

    /// <summary>班次窗口重叠会让时刻归属取决于集合顺序,构造时直接拒绝。</summary>
    private static void RejectOverlaps(IReadOnlyList<Shift> shifts)
    {
        var windows = new List<(string Code, int StartMinute, int EndMinute)>();
        foreach (Shift shift in shifts)
        {
            int duration = shift.CrossesMidnight
                ? 24 * 60 - (int)(shift.StartLocalTime - shift.EndLocalTime).TotalMinutes
                : (int)(shift.EndLocalTime - shift.StartLocalTime).TotalMinutes;

            // 三个相邻开班日的窗口足以暴露任何周期性重叠。
            for (int day = 0; day < 3; day++)
            {
                int start = day * 24 * 60 + (int)shift.StartLocalTime.ToTimeSpan().TotalMinutes;
                windows.Add((shift.Code, start, start + duration));
            }
        }

        for (int i = 0; i < windows.Count; i++)
        {
            for (int j = i + 1; j < windows.Count; j++)
            {
                if (windows[i].StartMinute < windows[j].EndMinute &&
                    windows[j].StartMinute < windows[i].EndMinute)
                {
                    throw new ArgumentException(
                        $"Shifts '{windows[i].Code}' and '{windows[j].Code}' overlap.", nameof(shifts));
                }
            }
        }
    }

    /// <summary>
    /// 解析 UTC 时刻所属的班次与生产日;日历有空档且时刻落在空档时返回 null。
    /// </summary>
    public ShiftPeriod? Resolve(DateTimeOffset utcInstant)
    {
        DateTimeOffset local = TimeZoneInfo.ConvertTime(utcInstant, _factoryTimeZone);

        // 候选开班日:本地当日与前一日(跨天夜班从前一日开始)。
        for (int dayOffset = 0; dayOffset >= -1; dayOffset--)
        {
            DateOnly productionDate = DateOnly.FromDateTime(local.DateTime).AddDays(dayOffset);

            foreach (Shift shift in _shifts)
            {
                DateTimeOffset startLocal = ToLocal(productionDate, shift.StartLocalTime);
                DateTimeOffset endLocal = shift.CrossesMidnight
                    ? ToLocal(productionDate.AddDays(1), shift.EndLocalTime)
                    : ToLocal(productionDate, shift.EndLocalTime);

                if (local >= startLocal && local < endLocal)
                {
                    return new ShiftPeriod(
                        shift.Code,
                        productionDate,
                        startLocal.ToUniversalTime(),
                        endLocal.ToUniversalTime());
                }
            }
        }

        return null;
    }

    /// <summary>UTC 时刻所属的生产日(开班日规则)。落在班次空档时归属本地日历日。</summary>
    public DateOnly GetProductionDate(DateTimeOffset utcInstant)
    {
        ShiftPeriod? period = Resolve(utcInstant);
        if (period is not null)
        {
            return period.ProductionDate;
        }

        DateTimeOffset local = TimeZoneInfo.ConvertTime(utcInstant, _factoryTimeZone);
        return DateOnly.FromDateTime(local.DateTime);
    }

    /// <summary>
    /// 某生产日的全部班次区间(债 #9:OEE 计划时间必须用区间集合求和,
    /// 不得用首尾包络——包络会把班次间空档算进计划时间)。
    /// </summary>
    public IReadOnlyList<ShiftPeriod> GetShiftPeriods(DateOnly productionDate)
    {
        return _shifts
            .Select(shift =>
            {
                DateTimeOffset startLocal = ToLocal(productionDate, shift.StartLocalTime);
                DateTimeOffset endLocal = shift.CrossesMidnight
                    ? ToLocal(productionDate.AddDays(1), shift.EndLocalTime)
                    : ToLocal(productionDate, shift.EndLocalTime);
                return new ShiftPeriod(shift.Code, productionDate, startLocal.ToUniversalTime(), endLocal.ToUniversalTime());
            })
            .OrderBy(period => period.StartUtc)
            .ToList();
    }

    /// <summary>某生产日的计划生产分钟数(班次时长之和,不含空档)。</summary>
    public double GetPlannedMinutes(DateOnly productionDate)
    {
        return GetShiftPeriods(productionDate).Sum(period => (period.EndUtc - period.StartUtc).TotalMinutes);
    }

    /// <summary>某生产日的 UTC 起止(首班开始到末班结束)。注意:这是包络,含班次间空档,只用于粗划界,OEE 禁用。</summary>
    public (DateTimeOffset StartUtc, DateTimeOffset EndUtc) GetProductionDayBoundsUtc(DateOnly productionDate)
    {
        DateTimeOffset start = _shifts
            .Select(shift => ToLocal(productionDate, shift.StartLocalTime))
            .Min();

        DateTimeOffset end = _shifts
            .Select(shift => shift.CrossesMidnight
                ? ToLocal(productionDate.AddDays(1), shift.EndLocalTime)
                : ToLocal(productionDate, shift.EndLocalTime))
            .Max();

        return (start.ToUniversalTime(), end.ToUniversalTime());
    }

    private DateTimeOffset ToLocal(DateOnly date, TimeOnly time)
    {
        var unspecified = date.ToDateTime(time, DateTimeKind.Unspecified);
        TimeSpan offset = _factoryTimeZone.GetUtcOffset(unspecified);
        return new DateTimeOffset(unspecified, offset);
    }
}
