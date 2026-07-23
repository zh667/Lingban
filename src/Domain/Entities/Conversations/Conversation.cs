namespace Lingban.Domain.Entities.Conversations;

public class Conversation : BaseAuditableEntity, ITenantEntity
{
    public string TenantId { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    /// <summary>属主(M4 债):逐对象授权,非属主视为不存在。</summary>
    public string OwnerUserId { get; set; } = string.Empty;

    public ICollection<ConversationMessage> Messages { get; private set; } = new List<ConversationMessage>();
}
