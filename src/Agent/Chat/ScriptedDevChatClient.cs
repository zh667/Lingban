using System.Text.RegularExpressions;
using Lingban.Application.Common;
using Microsoft.Extensions.AI;

namespace Lingban.Agent.Chat;

/// <summary>
/// 开发环境限定的确定性脚本模型(`Llm:Mode=scripted`,非 Development 拒绝启动):
/// 供 E2E 与本地演示在中转站不可用时走通完整管道。
/// 不违反铁律 #2:SSE 里的 token/工具事件全部真实产生,只是"模型"是台词固定的脚本;
/// 工具执行、事实校验、答案审计、HITL 全部走真实路径。
/// </summary>
public sealed class ScriptedDevChatClient : IChatClient
{
    private static readonly Regex WorkOrderCode = new(@"WO-[A-Z0-9-]+", RegexOptions.IgnoreCase);

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var history = messages.ToList();
        // 无状态阶段判断:出现工具结果说明本轮工具已执行,进入总结阶段。
        bool toolPhaseDone = history.Any(message => message.Contents.OfType<FunctionResultContent>().Any());
        string lastUser = history.LastOrDefault(message => message.Role == ChatRole.User)?.Text ?? string.Empty;
        bool isReport = lastUser.Contains("报工") && WorkOrderCode.IsMatch(lastUser);

        if (!toolPhaseDone)
        {
            var call = isReport
                ? new FunctionCallContent("scripted-call-1", "ReportProduction", new Dictionary<string, object?>
                {
                    ["workOrderCode"] = WorkOrderCode.Match(lastUser).Value.ToUpperInvariant(),
                    ["completed"] = 5,
                    ["qualified"] = 5
                })
                : new FunctionCallContent("scripted-call-1", ToolNames.GetTodayWorkOrders, new Dictionary<string, object?>());
            yield return new ChatResponseUpdate(ChatRole.Assistant, [call]);
            yield break;
        }

        string[] tokens = isReport
            ? ["报工提议已创建,", "请在下方确认卡上批准或拒绝;", "确认之前不会写入任何生产数据。"]
            : ["今天的工单已列在上方工具卡片中,", "数据经独立校验。"];
        foreach (string token in tokens)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, token);
        }
    }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Streaming only.");

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}
