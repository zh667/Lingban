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

public sealed record ToolCallEvent(string CallId, string ToolName, string ArgumentsJson) : AgentEvent;

public sealed record ToolResultEvent(
    string CallId,
    string ToolName,
    object Result,
    VerificationResult Verification,
    IReadOnlyList<string> ToolSql,
    IReadOnlyList<string> VerificationSql,
    long ElapsedMs) : AgentEvent;

public sealed record ToolErrorEvent(string CallId, string ToolName, string Message) : AgentEvent;

/// <summary>
/// 答案级审计(铁律 #1 的最后一环):流结束后核对最终答案里的数字
/// 是否都能在"已校验的工具数据"里找到出处;工具有 Discrepancy/Failed 时强制降级。
/// token 已经流出无法收回,审计结论随 done 前的该事件下发,由 UI/调用方呈现。
/// </summary>
public sealed record AnswerAuditEvent(
    bool Passed,
    IReadOnlyList<string> UnverifiedNumbers,
    IReadOnlyList<string> NonVerifiedTools,
    IReadOnlyList<string> InvalidCitations) : AgentEvent;

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
