namespace Lingban.Domain.Entities.Calendar;

/// <summary>
/// 班次定义(工厂本地时间)。班次是数据不是代码;EndLocalTime 小于等于 StartLocalTime 表示跨天夜班。
/// </summary>
public class Shift : BaseAuditableEntity, ITenantEntity
{
    public string TenantId { get; set; } = string.Empty;

    public string Code { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public TimeOnly StartLocalTime { get; set; }

    public TimeOnly EndLocalTime { get; set; }

    public bool IsActive { get; set; } = true;

    public bool CrossesMidnight => EndLocalTime <= StartLocalTime;
}
