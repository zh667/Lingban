using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Lingban.Application.Common;
using Lingban.Application.Common.Interfaces;
using Lingban.Application.Common.Verification;
using Lingban.Application.Equipment.Queries;
using Lingban.Application.Production.Queries;
using Lingban.Application.Quality.Queries;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;

namespace Lingban.Agent.Tools;

/// <summary>
/// lingban-mes MCP Server 工具面(铁律 #4:与进程内 Agent 循环共用同一批查询与校验,单一实现两处暴露)。
/// 每次调用独立 DI 作用域:钉死 AsOf → MediatR 查询 → FactVerifier 独立 SQL 复核 → 返回数据+校验结论+真实 SQL。
/// 全部只读(ReadOnly=true);错误以可执行的结构化消息返回。
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

    [McpServerTool(Name = "mes_get_today_work_orders", ReadOnly = true, Idempotent = false, OpenWorld = false)]
    [Description("查询今天的生产工单。\"今天\"按工厂班次日历切分(跨天夜班归开班日),不是自然日。返回工单清单与总数/进行中/已完工统计,附独立路径事实校验结论。")]
    public Task<string> GetTodayWorkOrdersAsync(
        [Description("产线编码,可选;如用户说\"3号线\"则传 \"3\"")] string? productionLineCode = null)
        => RunAsync(ToolNames.GetTodayWorkOrders, clock => new GetTodayWorkOrdersQuery(
            ProductionLineCode: Blank(productionLineCode), AsOfUtc: clock));

    [McpServerTool(Name = "mes_analyze_delayed_orders", ReadOnly = true, Idempotent = false, OpenWorld = false)]
    [Description("分析延期工单:未完结且已超过计划结束时间的工单,含延期小时数与所属产线编码;可按产线编码过滤。")]
    public Task<string> AnalyzeDelayedOrdersAsync(
        [Description("产线编码,可选")] string? productionLineCode = null)
        => RunAsync(ToolNames.AnalyzeDelayedOrders, clock => new AnalyzeDelayedOrdersQuery(
            clock, ProductionLineCode: Blank(productionLineCode)));

    [McpServerTool(Name = "mes_get_defect_summary", ReadOnly = true, Idempotent = false, OpenWorld = false)]
    [Description("统计近 N 天缺陷分布:按缺陷类型汇总数量与占比。")]
    public Task<string> GetDefectSummaryAsync(
        [Description("统计最近多少天,1-365,默认 7")] int days = 7)
        => RunAsync(ToolNames.GetDefectSummary, clock => new GetDefectSummaryQuery(days, clock));

    [McpServerTool(Name = "mes_calculate_oee", ReadOnly = true, Idempotent = false, OpenWorld = false)]
    [Description("计算某台设备指定生产日的 OEE:计划时间为班次区间之和,停机取区间并集;性能/质量为产线级归因,数据不足时为 null。")]
    public Task<string> CalculateOeeAsync(
        [Description("设备业务编码,如 EQ-1")] string equipmentCode,
        [Description("生产日,格式 yyyy-MM-dd,可选;不填按当前时刻推算")] string? productionDate = null)
    {
        DateOnly? date = null;
        if (!string.IsNullOrWhiteSpace(productionDate))
        {
            if (!DateOnly.TryParseExact(productionDate, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out DateOnly parsed))
            {
                return Task.FromResult(JsonSerializer.Serialize(new
                {
                    error = new { field = "productionDate", expected = "yyyy-MM-dd", given = productionDate, recoverable = true }
                }, JsonOptions));
            }

            date = parsed;
        }

        return RunAsync(ToolNames.CalculateOee, clock => new CalculateOeeQuery(
            EquipmentCode: equipmentCode, ProductionDate: date, AsOfUtc: clock));
    }

    private async Task<string> RunAsync(string toolName, Func<DateTimeOffset, object> buildQuery)
    {
        using IServiceScope scope = _scopeFactory.CreateScope();
        var timeProvider = scope.ServiceProvider.GetRequiredService<TimeProvider>();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var verifier = scope.ServiceProvider.GetRequiredService<IFactVerifier>();
        var queryLog = scope.ServiceProvider.GetRequiredService<IQueryLog>();
        var clock = scope.ServiceProvider.GetRequiredService<Lingban.Agent.Chat.IAgentInvocationClock>();

        DateTimeOffset asOf = timeProvider.GetUtcNow();
        clock.Pin(asOf);
        object query = buildQuery(asOf);

        try
        {
            int toolCheckpoint = queryLog.Checkpoint();
            object result = (await sender.Send(query))!;
            int verifyCheckpoint = queryLog.Checkpoint();
            VerificationResult verification = await verifier.VerifyAsync(toolName, query, result);

            return JsonSerializer.Serialize(new
            {
                asOfUtc = asOf,
                data = result,
                verification,
                toolSql = queryLog.Since(toolCheckpoint).Take(verifyCheckpoint - toolCheckpoint),
                verificationSql = queryLog.Since(verifyCheckpoint)
            }, JsonOptions);
        }
        catch (Exception exception) when (
            exception is Lingban.Application.Common.Exceptions.ValidationException
                or Ardalis.GuardClauses.NotFoundException
                or InvalidOperationException)
        {
            return JsonSerializer.Serialize(new
            {
                error = new { tool = toolName, message = exception.Message, recoverable = true }
            }, JsonOptions);
        }
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
