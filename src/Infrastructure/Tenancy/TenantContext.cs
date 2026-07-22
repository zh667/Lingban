using Lingban.Application.Common.Interfaces;
using Microsoft.Extensions.Configuration;

namespace Lingban.Infrastructure.Tenancy;

/// <summary>
/// M1 实现:租户来自配置(Tenancy:DefaultTenant,默认 "default")。
/// 鉴权接入后替换为从令牌/请求解析。
/// </summary>
public class TenantContext : ITenantContext
{
    public TenantContext(IConfiguration configuration)
    {
        TenantId = configuration["Tenancy:DefaultTenant"] ?? "default";
    }

    public string TenantId { get; }
}
