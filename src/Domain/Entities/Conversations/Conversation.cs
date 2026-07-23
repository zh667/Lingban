namespace Lingban.Domain.Entities.Conversations;

public class Conversation : BaseAuditableEntity, ITenantEntity
{
    public string TenantId { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public ICollection<ConversationMessage> Messages { get; private set; } = new List<ConversationMessage>();
}
