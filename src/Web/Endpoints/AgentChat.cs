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
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    // SSE 并发上限(五审遗留债):每用户至多 2 条活跃流,窗口限速管不住慢速长连接。
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> ActiveStreams = new();
    private const int MaxStreamsPerUser = 2;

    public static void Map(RouteGroupBuilder groupBuilder)
    {
        groupBuilder.MapPost(Chat, "/chat")
            .RequireAuthorization("MesData")
            .RequireRateLimiting("agent-chat");

        groupBuilder.MapPost(ConfirmAction, "/actions/{actionId:int}/confirm")
            .RequireAuthorization("MesData");
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
        if (ActiveStreams.AddOrUpdate(streamKey, 1, (_, count) => count + 1) > MaxStreamsPerUser)
        {
            ActiveStreams.AddOrUpdate(streamKey, 0, (_, count) => count - 1);
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
            ActiveStreams.AddOrUpdate(streamKey, 0, (_, count) => Math.Max(0, count - 1));
        }
    }
}
