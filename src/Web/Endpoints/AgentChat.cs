using System.Text.Json;
using Lingban.Agent.Chat;
using Microsoft.AspNetCore.Mvc;

namespace Lingban.Web.Endpoints;

/// <summary>
/// Agent 对话 SSE 端点:token/tool_call/tool_result/verification 事件原样透传。
/// M4 债已还:必须登录(Identity bearer),按属主访问会话,固定窗口限速。
/// </summary>
public class AgentChat : IEndpointGroup
{
    // 枚举一律字符串化:前端按 "Verified"/"Discrepancy" 等语义值分支,数字是隐式契约(E2E 抓获)。
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    // SSE 并发上限(五审遗留债):每用户至多 2 条活跃流,窗口限速管不住慢速长连接。
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> ActiveStreams = new();
    private const int MaxStreamsPerUser = 2;

    public static void Map(RouteGroupBuilder groupBuilder)
    {
        groupBuilder.MapPost(Chat, "/chat")
            .RequireAuthorization("MesData")
            .RequireRateLimiting("agent-chat");

        // 确认是生产写操作(八审 #3):MesData(含只读角色)不够,必须 MesWrite。
        groupBuilder.MapPost(ConfirmAction, "/actions/{actionId:int}/confirm")
            .RequireAuthorization("MesWrite");

        groupBuilder.MapGet(Status, "/status")
            .RequireAuthorization("MesData");
    }

    public record AgentStatusResponse(bool Scripted, string? Model);

    // 八审 #11 轻债:scripted 演示模式必须对观看者可见,防止把固定台词当真实模型能力。
    [EndpointSummary("Agent runtime status (scripted demo mode flag and model name)")]
    public static Microsoft.AspNetCore.Http.HttpResults.Ok<AgentStatusResponse> Status(IConfiguration configuration)
    {
        bool scripted = string.Equals(configuration["Llm:Mode"], "scripted", StringComparison.OrdinalIgnoreCase);
        return TypedResults.Ok(new AgentStatusResponse(scripted, scripted ? "scripted" : configuration["Llm:Model"]));
    }

    public record ConfirmRequest(bool Approve);

    [EndpointSummary("Confirm or reject a pending HITL action")]
    public static async Task<IResult> ConfirmAction(
        int actionId,
        [FromBody] ConfirmRequest request,
        MediatR.ISender sender,
        CancellationToken cancellationToken)
    {
        var action = await sender.Send(
            new Lingban.Application.Actions.ConfirmPendingActionCommand(actionId, request.Approve),
            cancellationToken);
        return Results.Ok(new { actionId = action.Id, status = action.Status.ToString(), result = action.ResultJson });
    }

    public record ChatRequest(int? ConversationId, string Message, Guid? ClientMessageId = null);

    [EndpointSummary("Chat with the Lingban agent (SSE stream)")]
    public static async Task Chat(
        HttpContext httpContext,
        [FromBody] ChatRequest request,
        IAgentChatService chatService,
        CancellationToken cancellationToken)
    {
        string streamKey = httpContext.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
            ?? httpContext.Connection.RemoteIpAddress?.ToString() ?? "anonymous";
        // 上限语义是"每实例"(八审 #8):进程内静态字典,多实例部署时各算各的;
        // 分布式租约版入债表,触发条件=第二个实例上线。
        if (ActiveStreams.AddOrUpdate(streamKey, 1, (_, count) => count + 1) > MaxStreamsPerUser)
        {
            ReleaseStream(streamKey);
            httpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            await httpContext.Response.WriteAsJsonAsync(new { error = "TOO_MANY_STREAMS", max = MaxStreamsPerUser }, cancellationToken);
            return;
        }

        try
        {
            httpContext.Response.ContentType = "text/event-stream";
            httpContext.Response.Headers.CacheControl = "no-cache";

            await foreach (AgentEvent agentEvent in chatService.ChatAsync(
                request.ConversationId, request.Message, request.ClientMessageId, cancellationToken))
            {
                (string eventName, object payload) = agentEvent switch
                {
                    TokenEvent token => ("token", (object)new { text = token.Text }),
                    ToolCallEvent call => ("tool_call", new { callId = call.CallId, tool = call.ToolName, arguments = call.ArgumentsJson }),
                    ToolResultEvent result => ("tool_result", new
                    {
                        callId = result.CallId,
                        tool = result.ToolName,
                        data = result.Result,
                        verification = result.Verification,
                        toolSql = result.ToolSql,
                        verificationSql = result.VerificationSql,
                        elapsedMs = result.ElapsedMs
                    }),
                    ToolErrorEvent toolError => ("tool_error", new { callId = toolError.CallId, tool = toolError.ToolName, message = toolError.Message }),
                    HitlPendingEvent hitl => ("hitl_pending", new { actionId = hitl.ActionId, actionType = hitl.ActionType, summary = hitl.Summary, payload = hitl.PayloadJson }),
                    AnswerAuditEvent auditEvent => ("answer_audit", new { passed = auditEvent.Passed, unverifiedNumbers = auditEvent.UnverifiedNumbers, nonVerifiedTools = auditEvent.NonVerifiedTools, invalidCitations = auditEvent.InvalidCitations }),
                    DoneEvent done => ("done", new { conversationId = done.ConversationId, messageId = done.AssistantMessageId }),
                    ErrorEvent error => ("error", new { message = error.Message }),
                    _ => ("unknown", new { })
                };

                await httpContext.Response.WriteAsync(
                    $"event: {eventName}\ndata: {JsonSerializer.Serialize(payload, JsonOptions)}\n\n",
                    cancellationToken);
                await httpContext.Response.Body.FlushAsync(cancellationToken);
            }
        }
        finally
        {
            ReleaseStream(streamKey);
        }
    }

    private static void ReleaseStream(string streamKey)
    {
        int remaining = ActiveStreams.AddOrUpdate(streamKey, 0, (_, count) => Math.Max(0, count - 1));
        if (remaining == 0)
        {
            // 条件移除:仅当值仍为 0 时删条目,避免零值条目随身份数量无限积累(八审 #8)。
            ((System.Collections.Generic.ICollection<System.Collections.Generic.KeyValuePair<string, int>>)ActiveStreams)
                .Remove(new System.Collections.Generic.KeyValuePair<string, int>(streamKey, 0));
        }
    }
}
