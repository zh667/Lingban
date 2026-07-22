namespace Lingban.Application.Common.Interfaces;

/// <summary>当前请求的租户上下文。M1 为配置的默认租户,鉴权接入后从令牌解析。</summary>
public interface ITenantContext
{
    string TenantId { get; }
}
