namespace Lingban.Domain.Common;

/// <summary>
/// 多租户实体标记。TenantId 由基础设施层在保存时盖章,并通过全局查询过滤器隔离。
/// </summary>
public interface ITenantEntity
{
    string TenantId { get; set; }
}
