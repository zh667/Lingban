namespace Lingban.Domain.Entities.Conversations;

public class ConversationMessage : BaseAuditableEntity, ITenantEntity
{
    public string TenantId { get; set; } = string.Empty;

    public int ConversationId { get; set; }

    public Conversation Conversation { get; set; } = null!;

    public ConversationRole Role { get; set; }

    public string Content { get; set; } = string.Empty;

    /// <summary>本条助手消息期间的工具调用结果(JSON,含校验结论与真实 SQL)。</summary>
    public string? ToolResultsJson { get; set; }

    /// <summary>客户端幂等键:同键重试不产生重复回合。</summary>
    public Guid? ClientMessageId { get; set; }
}
