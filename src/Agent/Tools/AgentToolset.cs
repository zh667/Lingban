using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Lingban.Agent.Chat;
using Lingban.Application.Common;
using Lingban.Application.Common.Interfaces;
using Lingban.Application.Common.Verification;
using Lingban.Application.Equipment.Queries;
using Lingban.Application.Production.Queries;
using Lingban.Application.Quality.Queries;
using MediatR;
using Microsoft.Extensions.AI;

namespace Lingban.Agent.Tools;

/// <summary>
/// LLM 工具集。每次执行:AsOf 钉死时钟 → MediatR 查询 → FactVerifier 独立复核 →
/// QueryLog 三段分账(工具 SQL / 校验 SQL)→ 带 CallId 的事件推给 UI。
/// 参数错误以结构化 JSON 回给模型(含字段与期望格式),模型可自愈重试。
/// 本里程碑只注册只读工具;写操作工具随 HITL 交互(M4/M6)进入。
/// </summary>
public class AgentToolset
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ISender _sender;
    private readonly IFactVerifier _factVerifier;
    private readonly IQueryLog _queryLog;
    private readonly IAgentInvocationClock _clock;
    private readonly List<AgentEvent> _pendingEvents = new();

    public AgentToolset(
        ISender sender,
        IFactVerifier factVerifier,
        IQueryLog queryLog,
        IAgentInvocationClock clock)
    {
        _sender = sender;
        _factVerifier = factVerifier;
        _queryLog = queryLog;
        _clock = clock;
    }

    public IReadOnlyList<AgentEvent> DrainEvents()
    {
        var drained = _pendingEvents.ToList();
        _pendingEvents.Clear();
        return drained;
    }

    public IList<AITool> BuildTools() => new List<AITool>
    {
        AIFunctionFactory.Create(GetTodayWorkOrdersAsync, ToolNames.GetTodayWorkOrders,
            "查询今天的生产工单。\"今天\"按工厂班次日历切分(跨天夜班归开班日),不是自然日。" +
            "返回工单清单与总数/进行中/已完工统计。可选按产线编码过滤(如用户说\"3号线\",传产线编码\"3\")。"),
        AIFunctionFactory.Create(AnalyzeDelayedOrdersAsync, ToolNames.AnalyzeDelayedOrders,
            "分析延期工单:所有未完结且已超过计划结束时间的工单,含延期小时数与所属产线编码。" +
            "可选按产线编码过滤。"),
        AIFunctionFactory.Create(GetDefectSummaryAsync, ToolNames.GetDefectSummary,
            "统计近 N 天缺陷分布:按缺陷类型汇总数量与占比(帕累托底料)。"),
        AIFunctionFactory.Create(CalculateOeeAsync, ToolNames.CalculateOee,
            "计算某台设备指定生产日的 OEE。设备用业务编码指定(如 \"EQ-1\");" +
            "计划时间为班次区间之和,停机取区间并集;性能/质量为产线级归因(Attribution 注明),数据不足时为 null。")
    };

    private Task<string> GetTodayWorkOrdersAsync(
        [Description("产线编码,可选;不填查全部产线")] string? productionLineCode = null)
    {
        var query = new GetTodayWorkOrdersQuery(
            ProductionLineCode: NullIfBlank(productionLineCode), AsOfUtc: _clock.AsOfUtc);
        return ExecuteAsync(ToolNames.GetTodayWorkOrders, query, () => _sender.Send(query));
    }

    private Task<string> AnalyzeDelayedOrdersAsync(
        [Description("产线编码,可选;不填查全部产线")] string? productionLineCode = null)
    {
        var query = new AnalyzeDelayedOrdersQuery(
            _clock.AsOfUtc, ProductionLineCode: NullIfBlank(productionLineCode));
        return ExecuteAsync(ToolNames.AnalyzeDelayedOrders, query, () => _sender.Send(query));
    }

    private Task<string> GetDefectSummaryAsync(
        [Description("统计最近多少天,1-365,默认 7")] int days = 7)
    {
        var query = new GetDefectSummaryQuery(days, _clock.AsOfUtc);
        return ExecuteAsync(ToolNames.GetDefectSummary, query, () => _sender.Send(query));
    }

    private Task<string> CalculateOeeAsync(
        [Description("设备业务编码,如 EQ-1")] string equipmentCode,
        [Description("生产日,格式 yyyy-MM-dd,可选;不填按当前时刻推算")] string? productionDate = null)
    {
        DateOnly? date = null;
        if (!string.IsNullOrWhiteSpace(productionDate))
        {
            if (!DateOnly.TryParseExact(productionDate, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out DateOnly parsed))
            {
                return Task.FromResult(ParameterError(
                    ToolNames.CalculateOee, "productionDate", "必须是 yyyy-MM-dd 格式的有效日期", productionDate));
            }

            date = parsed;
        }

        var query = new CalculateOeeQuery(
            EquipmentCode: NullIfBlank(equipmentCode), ProductionDate: date, AsOfUtc: _clock.AsOfUtc);
        return ExecuteAsync(ToolNames.CalculateOee, query, () => _sender.Send(query));
    }

    private async Task<string> ExecuteAsync<TResult>(
        string toolName, object request, Func<Task<TResult>> execute)
        where TResult : notnull
    {
        string callId = FunctionInvokingChatClient.CurrentContext?.CallContent.CallId ?? Guid.NewGuid().ToString("N");
        int toolCheckpoint = _queryLog.Checkpoint();
        long started = Stopwatch.GetTimestamp();

        TResult result;
        try
        {
            result = await execute();
        }
        catch (Exception exception) when (
            exception is Lingban.Application.Common.Exceptions.ValidationException
                or Ardalis.GuardClauses.NotFoundException
                or InvalidOperationException)
        {
            // 可恢复的参数/业务错误:结构化回给模型(不泄内部栈),模型可修正参数重试。
            _pendingEvents.Add(new ToolErrorEvent(callId, toolName, exception.Message));
            return JsonSerializer.Serialize(new
            {
                error = new { tool = toolName, message = exception.Message, recoverable = true }
            }, JsonOptions);
        }

        int verifyCheckpoint = _queryLog.Checkpoint();
        VerificationResult verification = await _factVerifier.VerifyAsync(toolName, request, result);
        IReadOnlyList<string> toolSql = _queryLog.Since(toolCheckpoint).Take(verifyCheckpoint - toolCheckpoint).ToList();
        IReadOnlyList<string> verificationSql = _queryLog.Since(verifyCheckpoint);
        long elapsedMs = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;

        _pendingEvents.Add(new ToolResultEvent(callId, toolName, result, verification, toolSql, verificationSql, elapsedMs));

        return JsonSerializer.Serialize(new
        {
            data = result,
            verification = new { status = verification.Status.ToString(), summary = verification.Summary }
        }, JsonOptions);
    }

    private static string ParameterError(string tool, string field, string expected, string given)
        => JsonSerializer.Serialize(new
        {
            error = new { tool, field, expected, given, recoverable = true }
        }, JsonOptions);

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
