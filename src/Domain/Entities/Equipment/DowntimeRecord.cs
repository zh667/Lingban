namespace Lingban.Domain.Entities.Equipment;

/// <summary>停机记录(采集事实,带来源标记)。</summary>
public class DowntimeRecord : BaseAuditableEntity, ITenantEntity
{
    public string TenantId { get; set; } = string.Empty;

    public int EquipmentId { get; set; }

    public Equipment Equipment { get; set; } = null!;

    public string Reason { get; set; } = string.Empty;

    public DateTimeOffset StartUtc { get; set; }

    public DateTimeOffset? EndUtc { get; set; }

    public DataSource Source { get; set; }

    public string? Description { get; set; }
}
