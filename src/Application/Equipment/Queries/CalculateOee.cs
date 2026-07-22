using Lingban.Application.Common.Interfaces;
using Lingban.Domain.Enums;
using Lingban.Domain.Services;

namespace Lingban.Application.Equipment.Queries;

public record ShiftPeriodDto(string ShiftCode, DateTimeOffset StartUtc, DateTimeOffset EndUtc);

public record OeeDto(
    int EquipmentId,
    string EquipmentCode,
    DateOnly ProductionDate,
    IReadOnlyList<ShiftPeriodDto> ShiftPeriods,
    double PlannedMinutes,
    double DowntimeMinutes,
    double RunningMinutes,
    double Availability,
    double? Performance,
    double? Quality,
    double? Oee,
    string Attribution);

/// <summary>
/// 设备 OEE。计划时间 = 班次区间之和(债 #9:不用首尾包络,空档不计入)。
/// Performance/Quality 按设备所在产线的工单四账做产线级归因——这是近似,
/// Attribution 字段如实标注;拿不到数据时相应分量为 null,OEE 不硬凑。
/// </summary>
public record CalculateOeeQuery(int EquipmentId, DateOnly? ProductionDate = null, DateTimeOffset? AsOfUtc = null)
    : IRequest<OeeDto>;

public class CalculateOeeQueryHandler : IRequestHandler<CalculateOeeQuery, OeeDto>
{
    private readonly IApplicationDbContext _context;
    private readonly IFactoryCalendarProvider _calendarProvider;
    private readonly TimeProvider _timeProvider;

    public CalculateOeeQueryHandler(
        IApplicationDbContext context,
        IFactoryCalendarProvider calendarProvider,
        TimeProvider timeProvider)
    {
        _context = context;
        _calendarProvider = calendarProvider;
        _timeProvider = timeProvider;
    }

    public async Task<OeeDto> Handle(CalculateOeeQuery request, CancellationToken cancellationToken)
    {
        var equipment = await _context.Equipment.AsNoTracking()
            .Where(item => item.Id == request.EquipmentId)
            .Select(item => new
            {
                item.Id,
                item.Code,
                item.IdealCycleTimeSeconds,
                LineId = item.Workstation != null ? (int?)item.Workstation.ProductionLineId : null
            })
            .FirstOrDefaultAsync(cancellationToken);
        Guard.Against.NotFound(request.EquipmentId, equipment);

        ShiftCalendar calendar = await _calendarProvider.GetCalendarAsync(cancellationToken);
        DateOnly productionDate = request.ProductionDate
            ?? calendar.GetProductionDate(request.AsOfUtc ?? _timeProvider.GetUtcNow());

        IReadOnlyList<ShiftPeriod> periods = calendar.GetShiftPeriods(productionDate);
        double plannedMinutes = periods.Sum(period => (period.EndUtc - period.StartUtc).TotalMinutes);
        DateTimeOffset dayStart = periods.Min(period => period.StartUtc);
        DateTimeOffset dayEnd = periods.Max(period => period.EndUtc);

        // 停机与班次区间的重叠时长(区间求交,空档里的停机不计入)。
        var downtimes = await _context.DowntimeRecords.AsNoTracking()
            .Where(record =>
                record.EquipmentId == equipment.Id &&
                record.StartUtc < dayEnd &&
                (record.EndUtc == null || record.EndUtc > dayStart))
            .Select(record => new { record.StartUtc, record.EndUtc })
            .ToListAsync(cancellationToken);

        double downtimeMinutes = 0d;
        foreach (var downtime in downtimes)
        {
            DateTimeOffset downtimeEnd = downtime.EndUtc ?? dayEnd;
            foreach (ShiftPeriod period in periods)
            {
                DateTimeOffset overlapStart = downtime.StartUtc > period.StartUtc ? downtime.StartUtc : period.StartUtc;
                DateTimeOffset overlapEnd = downtimeEnd < period.EndUtc ? downtimeEnd : period.EndUtc;
                if (overlapEnd > overlapStart)
                {
                    downtimeMinutes += (overlapEnd - overlapStart).TotalMinutes;
                }
            }
        }

        double runningMinutes = Math.Max(0d, plannedMinutes - downtimeMinutes);
        double availability = plannedMinutes > 0 ? runningMinutes / plannedMinutes : 0d;

        // 产线级归因:该设备所在产线、本生产日实际开工的工单四账。
        double? performance = null;
        double? quality = null;
        if (equipment.LineId is int lineId)
        {
            var lineTotals = await _context.WorkOrders.AsNoTracking()
                .Where(order =>
                    order.ProductionLineId == lineId &&
                    order.ActualStartUtc >= dayStart &&
                    order.ActualStartUtc < dayEnd)
                .GroupBy(_ => 1)
                .Select(group => new
                {
                    Completed = group.Sum(order => order.CompletedQuantity),
                    Qualified = group.Sum(order => order.QualifiedQuantity)
                })
                .FirstOrDefaultAsync(cancellationToken);

            if (lineTotals is not null && lineTotals.Completed > 0)
            {
                quality = (double)(lineTotals.Qualified / lineTotals.Completed);
                if (equipment.IdealCycleTimeSeconds > 0 && runningMinutes > 0)
                {
                    performance = (double)lineTotals.Completed * (double)equipment.IdealCycleTimeSeconds / 60d
                        / runningMinutes;
                }
            }
        }

        double? oee = performance is not null && quality is not null
            ? availability * performance.Value * quality.Value
            : null;

        return new OeeDto(
            equipment.Id,
            equipment.Code,
            productionDate,
            periods.Select(period => new ShiftPeriodDto(period.ShiftCode, period.StartUtc, period.EndUtc)).ToList(),
            plannedMinutes,
            downtimeMinutes,
            runningMinutes,
            availability,
            performance,
            quality,
            oee,
            Attribution: "line-level");
    }
}
