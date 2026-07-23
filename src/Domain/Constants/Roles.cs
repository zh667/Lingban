namespace Lingban.Domain.Constants;

public abstract class Roles
{
    public const string Administrator = nameof(Administrator);

    /// <summary>MES 数据读取角色:聊天与 MCP(HTTP)端点的准入门槛,由管理员授予。</summary>
    public const string MesReader = nameof(MesReader);

    /// <summary>知识库管理角色:上传/替换 SOP 的写权限(七审 #2:读角色不得投毒知识库)。</summary>
    public const string KnowledgeManager = nameof(KnowledgeManager);
}
