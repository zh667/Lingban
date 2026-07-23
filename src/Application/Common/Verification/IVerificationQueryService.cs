namespace Lingban.Application.Common.Verification;

public record TodayWorkOrderCounts(
    int Total,
    int InProgress,
    int Completed,
    decimal PlannedSum,
    decimal CompletedSum,
    decimal QualifiedSum,
    decimal ScrapSum);

public record DelayedOrderRow(int Id, DateTimeOffset PlannedEndUtc, decimal PlannedQuantity, decimal CompletedQuantity);

public record DefectTypeRow(string Code, decimal Quantity);

public record EquipmentProfile(int Id, string Code, int? ProductionLineId, decimal IdealCycleTimeSeconds);

public record LineProductionTotals(decimal Completed, decimal Qualified);

public record KnowledgeChunkRow(string DocumentTitle, string Section, string Text, bool HasEmbedding);

/// <summary>
/// 校验专用的独立查询路径:基础设施层用原生 SQL 实现,
/// 与工具查询的 LINQ 管道不共享任何查询构造代码。
/// </summary>
public interface IVerificationQueryService
{
    Task<int?> ResolveProductionLineIdByCodeAsync(string code, CancellationToken cancellationToken);

    Task<TodayWorkOrderCounts> CountTodayWorkOrdersAsync(
        DateTimeOffset fromUtc, DateTimeOffset toUtc, int? productionLineId, CancellationToken cancellationToken);

    Task<IReadOnlyList<DelayedOrderRow>> GetDelayedWorkOrderRowsAsync(
        DateTimeOffset asOfUtc, int? productionLineId, CancellationToken cancellationToken);

    Task<IReadOnlyList<DefectTypeRow>> GetDefectTypeRowsAsync(
        DateTimeOffset sinceUtc, DateTimeOffset asOfUtc, CancellationToken cancellationToken);

    Task<EquipmentProfile?> GetEquipmentProfileAsync(
        int? equipmentId, string? equipmentCode, CancellationToken cancellationToken);

    Task<LineProductionTotals> GetLineProductionTotalsAsync(
        int productionLineId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken cancellationToken);

    Task<KnowledgeChunkRow?> GetKnowledgeChunkAsync(int chunkId, CancellationToken cancellationToken);

    Task<int> CountRetrievableChunksAsync(CancellationToken cancellationToken);

    /// <summary>停机分钟数:原始区间并集(重叠不重复计),开放与越过 clipUtc 的区间都截断,再与班次区间求交。</summary>
    Task<double> SumDowntimeUnionMinutesAsync(
        int equipmentId,
        IReadOnlyList<(DateTimeOffset FromUtc, DateTimeOffset ToUtc)> periods,
        DateTimeOffset clipUtc,
        CancellationToken cancellationToken);
}
