using System.ComponentModel;
using System.Text.Json;
using Lingban.Agent.Chat;
using Lingban.Application.Common;
using Microsoft.Extensions.AI;

namespace Lingban.Agent.Tools;

/// <summary>
/// Agent 循环侧的薄适配层:执行全部委托 MesToolExecutor(单一实现),
/// 这里只做事件转发(带 CallId)与"回给模型的精简载荷"编码。
/// </summary>
public class AgentToolset
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly MesToolExecutor _executor;
    private readonly List<AgentEvent> _pendingEvents = new();

    public AgentToolset(MesToolExecutor executor) => _executor = executor;

    public IReadOnlyList<AgentEvent> DrainEvents()
    {
        var drained = _pendingEvents.ToList();
        _pendingEvents.Clear();
        return drained;
    }

    public IList<AITool> BuildTools() => new List<AITool>
    {
        AIFunctionFactory.Create(GetTodayWorkOrdersAsync, ToolNames.GetTodayWorkOrders, ToolDescriptions.GetTodayWorkOrders),
        AIFunctionFactory.Create(AnalyzeDelayedOrdersAsync, ToolNames.AnalyzeDelayedOrders, ToolDescriptions.AnalyzeDelayedOrders),
        AIFunctionFactory.Create(GetDefectSummaryAsync, ToolNames.GetDefectSummary, ToolDescriptions.GetDefectSummary),
        AIFunctionFactory.Create(CalculateOeeAsync, ToolNames.CalculateOee, ToolDescriptions.CalculateOee),
        AIFunctionFactory.Create(SearchKnowledgeAsync, ToolNames.SearchKnowledge, ToolDescriptions.SearchKnowledge)
    };

    private async Task<string> GetTodayWorkOrdersAsync(
        [Description("产线编码,可选;不填查全部产线")] string? productionLineCode = null,
        CancellationToken cancellationToken = default)
        => Render(await _executor.GetTodayWorkOrdersAsync(productionLineCode, cancellationToken));

    private async Task<string> AnalyzeDelayedOrdersAsync(
        [Description("产线编码,可选;不填查全部产线")] string? productionLineCode = null,
        CancellationToken cancellationToken = default)
        => Render(await _executor.AnalyzeDelayedOrdersAsync(productionLineCode, cancellationToken));

    private async Task<string> GetDefectSummaryAsync(
        [Description("统计最近多少天,1-365,默认 7")] int days = 7,
        CancellationToken cancellationToken = default)
        => Render(await _executor.GetDefectSummaryAsync(days, cancellationToken));

    private async Task<string> SearchKnowledgeAsync(
        [Description("检索问题,自然语言")] string query,
        [Description("返回分块数,1-20,默认 5")] int topK = 5,
        CancellationToken cancellationToken = default)
        => Render(await _executor.SearchKnowledgeAsync(query, topK, cancellationToken));

    private async Task<string> CalculateOeeAsync(
        [Description("设备业务编码,如 EQ-1")] string equipmentCode,
        [Description("生产日,格式 yyyy-MM-dd,可选;不填按当前时刻推算")] string? productionDate = null,
        CancellationToken cancellationToken = default)
        => Render(await _executor.CalculateOeeAsync(equipmentCode, productionDate, cancellationToken));

    private string Render(MesToolExecution execution)
    {
        string callId = FunctionInvokingChatClient.CurrentContext?.CallContent.CallId ?? Guid.NewGuid().ToString("N");

        if (!execution.Success)
        {
            _pendingEvents.Add(new ToolErrorEvent(callId, execution.ToolName, execution.Error!.Message));
            return JsonSerializer.Serialize(new { error = execution.Error }, JsonOptions);
        }

        _pendingEvents.Add(new ToolResultEvent(
            callId, execution.ToolName, execution.Result!, execution.Verification!,
            execution.ToolSql, execution.VerificationSql, execution.ElapsedMs));

        // 回给模型:数据 + 校验结论;SQL 只进 UI 事件,不烧 token。
        return JsonSerializer.Serialize(new
        {
            data = execution.Result,
            verification = new
            {
                status = execution.Verification!.Status.ToString(),
                summary = execution.Verification.Summary
            }
        }, JsonOptions);
    }
}
