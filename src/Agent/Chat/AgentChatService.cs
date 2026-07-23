using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ardalis.GuardClauses;
using Lingban.Agent.Tools;
using Lingban.Application.Common.Interfaces;
using Lingban.Domain.Entities.Conversations;
using Lingban.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;

namespace Lingban.Agent.Chat;

public interface IAgentChatService
{
    IAsyncEnumerable<AgentEvent> ChatAsync(
        int? conversationId, string userMessage, CancellationToken cancellationToken = default);
}

/// <summary>
/// Agent 主循环:LLM 自主选择工具(旧项目"模式由用户选 + 关键词路由"的病根治点)。
/// Token 事件是模型真实增量;工具循环由 FunctionInvokingChatClient 驱动,
/// 工具内部完成 AsOf 钉死、事实校验与真实 SQL 采集。
/// </summary>
public class AgentChatService : IAgentChatService
{
    private const int ContextWindowMessages = 10;
    private const int MaxTitleLength = 30;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private const string SystemPrompt =
        "你是 Lingban(领班),制造运营助手。回答车间生产、质量、设备与延期问题。" +
        "规则:1) 一切数字与事实必须来自工具结果,禁止编造;" +
        "2) 每个工具结果附带 verification(独立查询路径的事实校验),回答中必须如实转述校验状态:" +
        "Verified 说明数字已复核;Discrepancy 必须明确警告数据存在出入;Unverified 说明该结果未经复核;" +
        "3) 工具没有返回的数据,直说不知道;4) 用简洁的中文回答,先结论后细节。";

    private readonly IChatClient _chatClient;
    private readonly AgentToolset _toolset;
    private readonly IAgentInvocationClock _clock;
    private readonly IApplicationDbContext _context;
    private readonly TimeProvider _timeProvider;

    public AgentChatService(
        IChatClient chatClient,
        AgentToolset toolset,
        IAgentInvocationClock clock,
        IApplicationDbContext context,
        TimeProvider timeProvider)
    {
        _chatClient = chatClient;
        _toolset = toolset;
        _clock = clock;
        _context = context;
        _timeProvider = timeProvider;
    }

    public async IAsyncEnumerable<AgentEvent> ChatAsync(
        int? conversationId,
        string userMessage,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userMessage))
        {
            yield return new ErrorEvent("Message is required.");
            yield break;
        }

        userMessage = userMessage.Trim();

        // 三审设计约束:进入循环先钉死"现在",本次调用内全部工具与校验共用。
        _clock.Pin(_timeProvider.GetUtcNow());

        Conversation conversation = await LoadOrCreateConversationAsync(conversationId, userMessage, cancellationToken);

        List<ChatMessage> messages = await BuildContextAsync(conversation, cancellationToken);
        messages.Add(new ChatMessage(ChatRole.User, userMessage));

        conversation.Messages.Add(new ConversationMessage
        {
            ConversationId = conversation.Id,
            Role = ConversationRole.User,
            Content = userMessage
        });
        await _context.SaveChangesAsync(cancellationToken);

        var chatOptions = new ChatOptions { Tools = _toolset.BuildTools() };
        var answer = new StringBuilder();
        var toolResults = new List<ToolResultEvent>();

        IAsyncEnumerable<ChatResponseUpdate> stream =
            _chatClient.GetStreamingResponseAsync(messages, chatOptions, cancellationToken);

        await foreach (ChatResponseUpdate update in stream)
        {
            foreach (AgentEvent pending in _toolset.DrainEvents())
            {
                if (pending is ToolResultEvent toolResult)
                {
                    toolResults.Add(toolResult);
                }

                yield return pending;
            }

            foreach (AIContent content in update.Contents)
            {
                switch (content)
                {
                    case FunctionCallContent call:
                        yield return new ToolCallEvent(
                            call.Name,
                            call.Arguments is null ? "{}" : JsonSerializer.Serialize(call.Arguments, JsonOptions));
                        break;

                    case TextContent text when text.Text.Length > 0:
                        answer.Append(text.Text);
                        yield return new TokenEvent(text.Text);
                        break;
                }
            }
        }

        foreach (AgentEvent pending in _toolset.DrainEvents())
        {
            if (pending is ToolResultEvent lateResult)
            {
                toolResults.Add(lateResult);
            }

            yield return pending;
        }

        var assistantMessage = new ConversationMessage
        {
            ConversationId = conversation.Id,
            Role = ConversationRole.Assistant,
            Content = answer.ToString(),
            ToolResultsJson = toolResults.Count == 0
                ? null
                : JsonSerializer.Serialize(
                    toolResults.Select(result => new
                    {
                        tool = result.ToolName,
                        data = result.Result,
                        verification = result.Verification,
                        executedSql = result.ExecutedSql,
                        elapsedMs = result.ElapsedMs
                    }), JsonOptions)
        };
        conversation.Messages.Add(assistantMessage);
        await _context.SaveChangesAsync(cancellationToken);

        yield return new DoneEvent(conversation.Id, assistantMessage.Id);
    }

    private async Task<Conversation> LoadOrCreateConversationAsync(
        int? conversationId, string userMessage, CancellationToken cancellationToken)
    {
        if (conversationId is int id)
        {
            Conversation? existing = await _context.Conversations
                .FirstOrDefaultAsync(conversation => conversation.Id == id, cancellationToken);
            Guard.Against.NotFound(id, existing);
            return existing;
        }

        var created = new Conversation
        {
            Title = userMessage.Length <= MaxTitleLength
                ? userMessage
                : userMessage[..(MaxTitleLength - 1)] + "…"
        };
        _context.Conversations.Add(created);
        await _context.SaveChangesAsync(cancellationToken);
        return created;
    }

    /// <summary>多轮上下文真实喂给模型(旧项目存了历史却从不使用的病根治点)。</summary>
    private async Task<List<ChatMessage>> BuildContextAsync(
        Conversation conversation, CancellationToken cancellationToken)
    {
        var messages = new List<ChatMessage> { new(ChatRole.System, SystemPrompt) };

        List<ConversationMessage> history = await _context.ConversationMessages
            .AsNoTracking()
            .Where(message => message.ConversationId == conversation.Id)
            .OrderByDescending(message => message.Created)
            .Take(ContextWindowMessages)
            .ToListAsync(cancellationToken);

        foreach (ConversationMessage message in history.OrderBy(message => message.Created))
        {
            messages.Add(new ChatMessage(
                message.Role == ConversationRole.User ? ChatRole.User : ChatRole.Assistant,
                message.Content));
        }

        return messages;
    }
}
