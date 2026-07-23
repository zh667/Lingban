using System.Text.Json;
using Lingban.Agent.Chat;
using Microsoft.AspNetCore.Mvc;

namespace Lingban.Web.Endpoints;

/// <summary>
/// Agent 对话 SSE 端点:token/tool_call/tool_result/verification 事件原样透传。
/// 匿名访问仅限当前阶段(单租户开发期);M4 MCP/鉴权边界落地时收紧——已记入债表。
/// </summary>
public class AgentChat : IEndpointGroup
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static void Map(RouteGroupBuilder groupBuilder)
    {
        groupBuilder.MapPost(Chat, "/chat").AllowAnonymous();
    }

    public record ChatRequest(int? ConversationId, string Message);

    [EndpointSummary("Chat with the Lingban agent (SSE stream)")]
    public static async Task Chat(
        HttpContext httpContext,
        [FromBody] ChatRequest request,
        IAgentChatService chatService,
        CancellationToken cancellationToken)
    {
        httpContext.Response.ContentType = "text/event-stream";
        httpContext.Response.Headers.CacheControl = "no-cache";

        await foreach (AgentEvent agentEvent in chatService.ChatAsync(
            request.ConversationId, request.Message, cancellationToken))
        {
            (string eventName, object payload) = agentEvent switch
            {
                TokenEvent token => ("token", (object)new { text = token.Text }),
                ToolCallEvent call => ("tool_call", new { tool = call.ToolName, arguments = call.ArgumentsJson }),
                ToolResultEvent result => ("tool_result", new
                {
                    tool = result.ToolName,
                    data = result.Result,
                    verification = result.Verification,
                    executedSql = result.ExecutedSql,
                    elapsedMs = result.ElapsedMs
                }),
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
}
