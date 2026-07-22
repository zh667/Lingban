namespace Lingban.Application.Common.Verification;

public record TodayWorkOrderCounts(int Total, int InProgress, int Completed);

/// <summary>
/// 校验专用的独立查询路径:基础设施层用原生 SQL 实现,
/// 与工具查询的 LINQ 管道不共享任何查询构造代码。
/// </summary>
public interface IVerificationQueryService
{
    Task<TodayWorkOrderCounts> CountTodayWorkOrdersAsync(
        DateTimeOffset fromUtc, DateTimeOffset toUtc, int? productionLineId, CancellationToken cancellationToken);

    Task<IReadOnlyList<int>> GetDelayedWorkOrderIdsAsync(DateTimeOffset asOfUtc, CancellationToken cancellationToken);

    Task<decimal> SumDefectQuantityBetweenAsync(
        DateTimeOffset sinceUtc, DateTimeOffset asOfUtc, CancellationToken cancellationToken);

    /// <summary>停机分钟数:先取原始区间做并集(重叠不重复计),开放区间截断到 clipUtc,再与各班次区间求交。</summary>
    Task<double> SumDowntimeUnionMinutesAsync(
        int equipmentId,
        IReadOnlyList<(DateTimeOffset FromUtc, DateTimeOffset ToUtc)> periods,
        DateTimeOffset clipUtc,
        CancellationToken cancellationToken);
}
