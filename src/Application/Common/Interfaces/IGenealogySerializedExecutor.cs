namespace Lingban.Application.Common.Interfaces;

/// <summary>
/// 谱系写入串行化闸门:同租户的"检测 + 写边"在同一把事务级锁内执行,
/// 消灭幂等重放、环检测、完工校验三处 check-then-act 竞态(Codex 二审 #1/#2/#3)。
/// 实现为 PostgreSQL pg_advisory_xact_lock,锁随事务提交/回滚自动释放。
/// </summary>
public interface IGenealogySerializedExecutor
{
    Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken);
}
