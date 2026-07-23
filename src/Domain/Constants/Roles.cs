namespace Lingban.Domain.Constants;

public abstract class Roles
{
    public const string Administrator = nameof(Administrator);

    /// <summary>MES 数据读取角色:聊天与 MCP(HTTP)端点的准入门槛,由管理员授予。</summary>
    public const string MesReader = nameof(MesReader);
}
