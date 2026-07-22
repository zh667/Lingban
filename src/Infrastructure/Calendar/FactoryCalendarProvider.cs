using Lingban.Application.Common.Interfaces;
using Lingban.Domain.Services;
using Lingban.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Lingban.Infrastructure.Calendar;

public class FactoryCalendarProvider : IFactoryCalendarProvider
{
    private readonly ApplicationDbContext _context;
    private readonly TimeZoneInfo _timeZone;

    public FactoryCalendarProvider(ApplicationDbContext context, IConfiguration configuration)
    {
        _context = context;
        _timeZone = TimeZoneInfo.FindSystemTimeZoneById(configuration["Factory:TimeZone"] ?? "Asia/Shanghai");
    }

    public async Task<ShiftCalendar> GetCalendarAsync(CancellationToken cancellationToken = default)
    {
        var shifts = await _context.Shifts.AsNoTracking()
            .Where(shift => shift.IsActive)
            .OrderBy(shift => shift.StartLocalTime)
            .ToListAsync(cancellationToken);

        return new ShiftCalendar(_timeZone, shifts);
    }
}
