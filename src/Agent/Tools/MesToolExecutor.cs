using System.Diagnostics;
using System.Globalization;
using Lingban.Agent.Chat;
using Lingban.Application.Common;
using Lingban.Application.Common.Interfaces;
using Lingban.Application.Common.Verification;
using Lingban.Application.Equipment.Queries;
using Lingban.Application.Production.Queries;
using Lingban.Application.Quality.Queries;
using MediatR;

namespace Lingban.Agent.Tools;

public sealed record MesToolError(
    string Tool, string Message, bool Recoverable, string? Field = null, string? Expected = null, string? Given = null);

public sealed record MesToolExecution(
    string ToolName,
    object? Result,
    VerificationResult? Verification,
    IReadOnlyList<string> ToolSql,
    IReadOnlyList<string> VerificationSql,
    long ElapsedMs,
    MesToolError? Error)
{
    public bool Success => Error is null;
}

/// <summary>共享的工具文案:两个暴露面(Agent 循环 / MCP)必须一字不差。</summary>
public static class ToolDescriptions
{
    public const string GetTodayWorkOrders =
        "查询今天的生产工单。\"今天\"按工厂班次日历切分(跨天夜班归开班日),不是自然日。" +
        "返回工单清单与总数/进行中/已完工统计。可选按产线编码过滤(如用户说\"3号线\",传产线编码\"3\")。";

    public const string AnalyzeDelayedOrders =
        "分析延期工单:所有未完结且已超过计划结束时间的工单,含延期小时数与所属产线编码。可选按产线编码过滤。";

    public const string GetDefectSummary = "统计近 N 天缺陷分布:按缺陷类型汇总数量与占比(帕累托底料)。";

    public const string CalculateOee =
        "计算某台设备指定生产日的 OEE。设备用业务编码指定(如 \"EQ-1\");" +
        "计划时间为班次区间之和,停机取区间并集;性能/质量为产线级归因(Attribution 注明),数据不足时为 null。";
}

/// <summary>
/// 工具执行内核——铁律 #4 的"单一实现":参数归一化、日期解析、查询构造、
/// FactVerifier 编排、SQL 三段分账、错误分类全部只此一处。
/// Agent 循环与 MCP Server 是它上面的薄适配层(协议注解、事件转发、编码)。
/// </summary>
public class MesToolExecutor
{
    private readonly ISender _sender;
    private readonly IFactVerifier _factVerifier;
    private readonly IQueryLog _queryLog;
    private readonly IAgentInvocationClock _clock;

    public MesToolExecutor(
        ISender sender, IFactVerifier factVerifier, IQueryLog queryLog, IAgentInvocationClock clock)
    {
        _sender = sender;
        _factVerifier = factVerifier;
        _queryLog = queryLog;
        _clock = clock;
    }

    public Task<MesToolExecution> GetTodayWorkOrdersAsync(string? productionLineCode, CancellationToken cancellationToken)
    {
        var query = new GetTodayWorkOrdersQuery(
            ProductionLineCode: Blank(productionLineCode), AsOfUtc: _clock.AsOfUtc);
        return RunAsync(ToolNames.GetTodayWorkOrders, query, token => _sender.Send(query, token), cancellationToken);
    }

    public Task<MesToolExecution> AnalyzeDelayedOrdersAsync(string? productionLineCode, CancellationToken cancellationToken)
    {
        var query = new AnalyzeDelayedOrdersQuery(_clock.AsOfUtc, ProductionLineCode: Blank(productionLineCode));
        return RunAsync(ToolNames.AnalyzeDelayedOrders, query, token => _sender.Send(query, token), cancellationToken);
    }

    public Task<MesToolExecution> GetDefectSummaryAsync(int days, CancellationToken cancellationToken)
    {
        var query = new GetDefectSummaryQuery(days, _clock.AsOfUtc);
        return RunAsync(ToolNames.GetDefectSummary, query, token => _sender.Send(query, token), cancellationToken);
    }

    public Task<MesToolExecution> CalculateOeeAsync(
        string? equipmentCode, string? productionDate, CancellationToken cancellationToken)
    {
        DateOnly? date = null;
        if (!string.IsNullOrWhiteSpace(productionDate))
        {
            if (!DateOnly.TryParseExact(productionDate, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out DateOnly parsed))
            {
                return Task.FromResult(ErrorExecution(new MesToolError(
                    ToolNames.CalculateOee, "生产日必须是 yyyy-MM-dd 格式的有效日期", Recoverable: true,
                    Field: "productionDate", Expected: "yyyy-MM-dd", Given: productionDate)));
            }

            date = parsed;
        }

        var query = new CalculateOeeQuery(
            EquipmentCode: Blank(equipmentCode), ProductionDate: date, AsOfUtc: _clock.AsOfUtc);
        return RunAsync(ToolNames.CalculateOee, query, token => _sender.Send(query, token), cancellationToken);
    }

    private async Task<MesToolExecution> RunAsync<TResult>(
        string toolName, object request, Func<CancellationToken, Task<TResult>> execute, CancellationToken cancellationToken)
        where TResult : notnull
    {
        int toolCheckpoint = _queryLog.Checkpoint();
        long started = Stopwatch.GetTimestamp();

        TResult result;
        try
        {
            result = await execute(cancellationToken);
        }
        catch (Exception exception) when (
            exception is Lingban.Application.Common.Exceptions.ValidationException
                or Ardalis.GuardClauses.NotFoundException
                or InvalidOperationException)
        {
            // 领域/校验层抛出的才原文透出;来源不明的 InvalidOperationException(如 EF 内部)给稳定错误码。
            bool domainOrigin = exception.TargetSite?.DeclaringType?.Namespace?.StartsWith("Lingban", StringComparison.Ordinal) == true
                || exception is Lingban.Application.Common.Exceptions.ValidationException
                or Ardalis.GuardClauses.NotFoundException;
            return ErrorExecution(new MesToolError(
                toolName,
                domainOrigin ? exception.Message : "内部错误(INTERNAL_QUERY_ERROR),请调整参数或稍后重试。",
                Recoverable: domainOrigin));
        }

        int verifyCheckpoint = _queryLog.Checkpoint();
        VerificationResult verification = await _factVerifier.VerifyAsync(toolName, request, result, cancellationToken);

        return new MesToolExecution(
            toolName,
            result,
            verification,
            _queryLog.Since(toolCheckpoint).Take(verifyCheckpoint - toolCheckpoint).ToList(),
            _queryLog.Since(verifyCheckpoint),
            (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            Error: null);
    }

    private static MesToolExecution ErrorExecution(MesToolError error) => new(
        error.Tool, null, null, Array.Empty<string>(), Array.Empty<string>(), 0, error);

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
