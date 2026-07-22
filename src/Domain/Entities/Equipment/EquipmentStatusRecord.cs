namespace Lingban.Domain.Entities.Equipment;

/// <summary>设备状态区间(采集事实,带来源标记)。</summary>
public class EquipmentStatusRecord : BaseAuditableEntity, ITenantEntity
{
    public string TenantId { get; set; } = string.Empty;

    public int EquipmentId { get; set; }

    public Equipment Equipment { get; set; } = null!;

    public EquipmentState State { get; set; }

    public DateTimeOffset StartUtc { get; set; }

    public DateTimeOffset? EndUtc { get; set; }

    public DataSource Source { get; set; }

    public string? Remarks { get; set; }
}
