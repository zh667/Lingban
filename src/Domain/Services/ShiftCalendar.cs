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

    /// <summary>某生产日的 UTC 起止(首班开始到末班结束),供"今天的工单/OEE"查询划界。</summary>
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
