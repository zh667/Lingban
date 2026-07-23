using System.ComponentModel;
using System.Diagnostics;
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
/// LLM 工具集(Agent 铁律 #3 四件套之 Description + 执行绑定)。
/// 每次工具执行:AsOf 用调用级钉死时钟 → MediatR 查询 → FactVerifier 独立复核 →
/// QueryLog 分段抓取真实 SQL(债 #12)→ 事件推给 UI,精简结果+校验结论回给模型。
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

    /// <summary>工具执行期间产生的事件;循环在每个流式分片间隙取走。</summary>
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
            "返回工单清单与总数/进行中/已完工统计。可选按产线过滤。"),
        AIFunctionFactory.Create(AnalyzeDelayedOrdersAsync, ToolNames.AnalyzeDelayedOrders,
            "分析延期工单:所有未完结且已超过计划结束时间的工单,含延期小时数。"),
        AIFunctionFactory.Create(GetDefectSummaryAsync, ToolNames.GetDefectSummary,
            "统计近 N 天缺陷分布:按缺陷类型汇总数量与占比(帕累托底料)。"),
        AIFunctionFactory.Create(CalculateOeeAsync, ToolNames.CalculateOee,
            "计算某台设备指定生产日的 OEE:计划时间为班次区间之和,停机取区间并集;" +
            "性能/质量为产线级归因(Attribution 字段注明),数据不足时为 null。")
    };

    private Task<string> GetTodayWorkOrdersAsync(
        [Description("产线 ID,可选;不填则查全部产线")] int? productionLineId = null)
    {
        var query = new GetTodayWorkOrdersQuery(productionLineId, _clock.AsOfUtc);
        return ExecuteAsync(ToolNames.GetTodayWorkOrders, query, () => _sender.Send(query));
    }

    private Task<string> AnalyzeDelayedOrdersAsync()
    {
        var query = new AnalyzeDelayedOrdersQuery(_clock.AsOfUtc);
        return ExecuteAsync(ToolNames.AnalyzeDelayedOrders, query, () => _sender.Send(query));
    }

    private Task<string> GetDefectSummaryAsync(
        [Description("统计最近多少天,1-365,默认 7")] int days = 7)
    {
        var query = new GetDefectSummaryQuery(days, _clock.AsOfUtc);
        return ExecuteAsync(ToolNames.GetDefectSummary, query, () => _sender.Send(query));
    }

    private Task<string> CalculateOeeAsync(
        [Description("设备 ID")] int equipmentId,
        [Description("生产日(yyyy-MM-dd),可选;不填按钉死的当前时刻推算")] string? productionDate = null)
    {
        DateOnly? date = productionDate is null ? null : DateOnly.Parse(productionDate);
        var query = new CalculateOeeQuery(equipmentId, date, _clock.AsOfUtc);
        return ExecuteAsync(ToolNames.CalculateOee, query, () => _sender.Send(query));
    }

    private async Task<string> ExecuteAsync<TResult>(
        string toolName, object request, Func<Task<TResult>> execute)
        where TResult : notnull
    {
        int checkpoint = _queryLog.Checkpoint();
        long started = Stopwatch.GetTimestamp();

        TResult result = await execute();
        VerificationResult verification = await _factVerifier.VerifyAsync(toolName, request, result);
        IReadOnlyList<string> executedSql = _queryLog.Since(checkpoint);
        long elapsedMs = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;

        _pendingEvents.Add(new ToolResultEvent(toolName, result, verification, executedSql, elapsedMs));

        // 回给模型:数据 + 校验结论(模型须在回答中如实转述校验状态);SQL 只进 UI 事件,不烧 token。
        return JsonSerializer.Serialize(new
        {
            data = result,
            verification = new { status = verification.Status.ToString(), summary = verification.Summary }
        }, JsonOptions);
    }
}
