using Lingban.Application.Common.Interfaces;

namespace Lingban.Application.Common.Models;

/// <summary>
/// 非交互宿主(MCP stdio / 模拟器)的进程身份:审计字段可追溯到服务名,无任何角色。
/// 六审 #1:缺少 IUser 注册曾让 stdio 工具调用在 DI 解析阶段全灭。
/// </summary>
public sealed class ServiceUser : IUser
{
    public ServiceUser(string id) => Id = id;

    public string? Id { get; }

    public List<string>? Roles => null;
}
