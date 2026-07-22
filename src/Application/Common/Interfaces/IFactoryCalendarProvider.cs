using Lingban.Domain.Services;

namespace Lingban.Application.Common.Interfaces;

/// <summary>从班次数据与工厂时区配置构建当前租户的 ShiftCalendar。</summary>
public interface IFactoryCalendarProvider
{
    Task<ShiftCalendar> GetCalendarAsync(CancellationToken cancellationToken = default);
}
