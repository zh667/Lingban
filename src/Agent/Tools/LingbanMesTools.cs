using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Lingban.Agent.Chat;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Lingban.Agent.Tools;

/// <summary>
/// lingban-mes MCP 侧的薄适配层:执行全部委托 MesToolExecutor(单一实现),
/// 这里只做协议编码——业务错误按 MCP 规范置 IsError=true,取消令牌贯穿到 EF 查询。
/// </summary>
[McpServerToolType]
public sealed class LingbanMesTools
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly IServiceScopeFactory _scopeFactory;

    public LingbanMesTools(IServiceScopeFactory scopeFactory) => _scopeFactory = scopeFactory;

    [McpServerTool(Name = "mes_get_today_work_orders", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description(ToolDescriptions.GetTodayWorkOrders)]
    public Task<CallToolResult> GetTodayWorkOrdersAsync(
        [Description("产线编码,可选;如用户说\"3号线\"则传 \"3\"")] string? productionLineCode = null,
        CancellationToken cancellationToken = default)
        => RunAsync((executor, token) => executor.GetTodayWorkOrdersAsync(productionLineCode, token), cancellationToken);

    [McpServerTool(Name = "mes_analyze_delayed_orders", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description(ToolDescriptions.AnalyzeDelayedOrders)]
    public Task<CallToolResult> AnalyzeDelayedOrdersAsync(
        [Description("产线编码,可选")] string? productionLineCode = null,
        CancellationToken cancellationToken = default)
        => RunAsync((executor, token) => executor.AnalyzeDelayedOrdersAsync(productionLineCode, token), cancellationToken);

    [McpServerTool(Name = "mes_get_defect_summary", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description(ToolDescriptions.GetDefectSummary)]
    public Task<CallToolResult> GetDefectSummaryAsync(
        [Description("统计最近多少天,1-365,默认 7")] int days = 7,
        CancellationToken cancellationToken = default)
        => RunAsync((executor, token) => executor.GetDefectSummaryAsync(days, token), cancellationToken);

    [McpServerTool(Name = "mes_calculate_oee", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description(ToolDescriptions.CalculateOee)]
    public Task<CallToolResult> CalculateOeeAsync(
        [Description("设备业务编码,如 EQ-1")] string equipmentCode,
        [Description("生产日,格式 yyyy-MM-dd,可选;不填按当前时刻推算")] string? productionDate = null,
        CancellationToken cancellationToken = default)
        => RunAsync((executor, token) => executor.CalculateOeeAsync(equipmentCode, productionDate, token), cancellationToken);

    private async Task<CallToolResult> RunAsync(
        Func<MesToolExecutor, CancellationToken, Task<MesToolExecution>> action,
        CancellationToken cancellationToken)
    {
        using IServiceScope scope = _scopeFactory.CreateScope();
        var timeProvider = scope.ServiceProvider.GetRequiredService<TimeProvider>();
        var clock = scope.ServiceProvider.GetRequiredService<IAgentInvocationClock>();
        clock.Pin(timeProvider.GetUtcNow());

        MesToolExecution execution = await action(
            scope.ServiceProvider.GetRequiredService<MesToolExecutor>(), cancellationToken);

        if (!execution.Success)
        {
            return new CallToolResult
            {
                IsError = true,
                Content = [new TextContentBlock { Text = JsonSerializer.Serialize(new { error = execution.Error }, JsonOptions) }]
            };
        }

        string payload = JsonSerializer.Serialize(new
        {
            asOfUtc = clock.AsOfUtc,
            data = execution.Result,
            verification = execution.Verification,
            toolSql = execution.ToolSql,
            verificationSql = execution.VerificationSql,
            elapsedMs = execution.ElapsedMs
        }, JsonOptions);

        return new CallToolResult { Content = [new TextContentBlock { Text = payload }] };
    }
}
