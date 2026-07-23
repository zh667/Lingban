namespace Lingban.Application.Common.Exceptions;

/// <summary>
/// 业务状态冲突(八审 #1/#9):目标资源已处于与请求相斥的状态(如挂起动作已被确认)。
/// Web 层映射为 409,前端据此提示"已处理",而不是笼统 500。
/// </summary>
public class ConflictException : Exception
{
    public ConflictException(string message)
        : base(message)
    {
    }
}
