using Lingban.Application.Common.Verification;

namespace Lingban.Agent.Chat;

public class LlmOptions
{
    public const string SectionName = "Llm";

    /// <summary>目前支持 openai-compatible;Anthropic 官方接入时扩展。</summary>
    public string Provider { get; set; } = "openai-compatible";

    public string BaseUrl { get; set; } = string.Empty;

    public string ApiKey { get; set; } = string.Empty;

    public string Model { get; set; } = string.Empty;
}

/// <summary>
/// Agent 流式事件。SSE 端点原样透传;Token 是模型真实增量,禁止伪造(Agent 铁律 #2)。
/// </summary>
public abstract record AgentEvent;

public sealed record TokenEvent(string Text) : AgentEvent;

public sealed record ToolCallEvent(string ToolName, string ArgumentsJson) : AgentEvent;

public sealed record ToolResultEvent(
    string ToolName,
    object Result,
    VerificationResult Verification,
    IReadOnlyList<string> ExecutedSql,
    long ElapsedMs) : AgentEvent;

public sealed record DoneEvent(int ConversationId, int AssistantMessageId) : AgentEvent;

public sealed record ErrorEvent(string Message) : AgentEvent;

/// <summary>
/// 单次 Agent 调用的钉死时钟(三审设计约束):
/// 循环开始时解析一次"现在",本次调用内所有工具共用,校验才可复现。
/// </summary>
public interface IAgentInvocationClock
{
    DateTimeOffset AsOfUtc { get; }

    void Pin(DateTimeOffset asOfUtc);
}

public class AgentInvocationClock : IAgentInvocationClock
{
    private DateTimeOffset? _pinned;

    public DateTimeOffset AsOfUtc => _pinned
        ?? throw new InvalidOperationException("AsOfUtc has not been pinned for this agent invocation.");

    public void Pin(DateTimeOffset asOfUtc) => _pinned = asOfUtc.ToUniversalTime();
}
