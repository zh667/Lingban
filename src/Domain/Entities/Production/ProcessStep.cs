namespace Lingban.Domain.Entities.Production;

public class ProcessStep : BaseAuditableEntity, ITenantEntity
{
    public string TenantId { get; set; } = string.Empty;

    public int ProcessRouteId { get; set; }

    public ProcessRoute ProcessRoute { get; set; } = null!;

    public int Sequence { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>默认执行工位;排程时可改派。</summary>
    public int? WorkstationId { get; set; }

    public Workstation? Workstation { get; set; }
}
