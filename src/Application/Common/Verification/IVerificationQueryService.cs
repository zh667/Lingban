namespace Lingban.Application.Common.Verification;

/// <summary>
/// 校验专用的独立查询路径:基础设施层用原生 SQL 实现,
/// 与工具查询的 LINQ 管道不共享任何查询构造代码。
/// </summary>
public interface IVerificationQueryService
{
    Task<int> CountWorkOrdersStartedBetweenAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken cancellationToken);

    Task<int> CountDelayedWorkOrdersAsync(DateTimeOffset asOfUtc, CancellationToken cancellationToken);

    Task<decimal> SumDefectQuantitySinceAsync(DateTimeOffset sinceUtc, CancellationToken cancellationToken);

    Task<double> SumDowntimeMinutesAsync(int equipmentId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken cancellationToken);
}
