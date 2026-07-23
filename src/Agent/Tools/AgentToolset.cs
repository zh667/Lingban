using System.ComponentModel;
using System.Text.Json;
using Lingban.Agent.Chat;
using Lingban.Application.Actions;
using Lingban.Application.Common;
using MediatR;
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
    private readonly Lingban.Application.Common.Interfaces.IUser _user;
    private readonly List<AgentEvent> _pendingEvents = new();

    public AgentToolset(MesToolExecutor executor, Lingban.Application.Common.Interfaces.IUser user)
    {
        _executor = executor;
        _user = user;
    }

    /// <summary>当前会话 ID,由 AgentChatService 在循环开始时设置(供 HITL 挂起动作关联)。</summary>
    public int? ConversationId { get; set; }

    public IReadOnlyList<AgentEvent> DrainEvents()
    {
        var drained = _pendingEvents.ToList();
        _pendingEvents.Clear();
        return drained;
    }

    public IList<AITool> BuildTools()
    {
        var tools = new List<AITool>
        {
            AIFunctionFactory.Create(GetTodayWorkOrdersAsync, ToolNames.GetTodayWorkOrders, ToolDescriptions.GetTodayWorkOrders),
            AIFunctionFactory.Create(AnalyzeDelayedOrdersAsync, ToolNames.AnalyzeDelayedOrders, ToolDescriptions.AnalyzeDelayedOrders),
            AIFunctionFactory.Create(GetDefectSummaryAsync, ToolNames.GetDefectSummary, ToolDescriptions.GetDefectSummary),
            AIFunctionFactory.Create(CalculateOeeAsync, ToolNames.CalculateOee, ToolDescriptions.CalculateOee),
            AIFunctionFactory.Create(SearchKnowledgeAsync, ToolNames.SearchKnowledge, ToolDescriptions.SearchKnowledge)
        };

        // 写工具按角色暴露(八审 #3):无写权限的用户连"提议"入口都不给,
        // 模型自然回答无此能力;命令层 [Authorize] 是第二道闸。
        bool canWrite = _user.Roles is { } roles
            && (roles.Contains(Lingban.Domain.Constants.Roles.Administrator)
                || roles.Contains(Lingban.Domain.Constants.Roles.ProductionReporter));
        if (canWrite)
        {
            tools.Add(AIFunctionFactory.Create(
                ProposeReportProductionAsync, ToolNames.ReportProduction, ToolDescriptions.ReportProduction));
        }

        return tools;
    }

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

    private async Task<string> ProposeReportProductionAsync(
        [Description("工单编号,如 WO-2607-01")] string workOrderCode,
        [Description("本次完工数量")] decimal completed,
        [Description("其中合格数量")] decimal qualified = 0,
        [Description("其中报废数量")] decimal scrap = 0,
        [Description("其中返工数量")] decimal rework = 0,
        CancellationToken cancellationToken = default)
    {
        // 单一实现在 MesToolExecutor(八审 #4):这里只做事件转发,与只读工具同构。
        MesToolExecution execution = await _executor.ProposeReportProductionAsync(
            workOrderCode, completed, qualified, scrap, rework, ConversationId, cancellationToken);
        if (execution.Success && execution.Result is ReportProductionProposalDto dto)
        {
            _pendingEvents.Add(new HitlPendingEvent(dto.ActionId, dto.ActionType, dto.Summary, dto.PayloadJson));
        }

        return Render(execution);
    }

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
