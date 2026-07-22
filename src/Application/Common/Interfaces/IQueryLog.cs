namespace Lingban.Application.Common.Interfaces;

/// <summary>
/// 当前作用域内实际执行过的 SQL(由 EF Core 拦截器捕获)。
/// Agent 铁律 #2:debug 信息必须来自真实执行的语句,禁止手写。
/// </summary>
public interface IQueryLog
{
    IReadOnlyList<string> Statements { get; }

    void Record(string statement);
}
