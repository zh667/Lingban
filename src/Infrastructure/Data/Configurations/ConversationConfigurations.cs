using Lingban.Domain.Entities.Conversations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Lingban.Infrastructure.Data.Configurations;

public class ConversationConfiguration : IEntityTypeConfiguration<Conversation>
{
    public void Configure(EntityTypeBuilder<Conversation> builder)
    {
        builder.Property(conversation => conversation.TenantId).HasMaxLength(64).IsRequired();
        builder.Property(conversation => conversation.Title).HasMaxLength(64).IsRequired();
        builder.Property(conversation => conversation.OwnerUserId).HasMaxLength(128).IsRequired();
        builder.HasIndex(conversation => new { conversation.TenantId, conversation.OwnerUserId });
        builder.HasAlternateKey(conversation => new { conversation.TenantId, conversation.Id });
    }
}

public class ConversationMessageConfiguration : IEntityTypeConfiguration<ConversationMessage>
{
    public void Configure(EntityTypeBuilder<ConversationMessage> builder)
    {
        builder.Property(message => message.TenantId).HasMaxLength(64).IsRequired();
        builder.Property(message => message.Content).IsRequired();

        builder.HasOne(message => message.Conversation)
            .WithMany(conversation => conversation.Messages)
            .HasForeignKey(message => new { message.TenantId, message.ConversationId })
            .HasPrincipalKey(conversation => new { conversation.TenantId, conversation.Id })
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(message => new { message.TenantId, message.ConversationId });

        // 幂等键唯一性由数据库强制(八审 #2):check-then-act 的并发窗口关死;
        // 键不含 ConversationId,首次请求重放(conversationId=null)同样撞索引。
        builder.Property(message => message.OwnerUserId).HasMaxLength(128);
        // 索引名是代码契约(九审 #2):AgentChatService 按此名精确识别幂等冲突,改名必须同步。
        builder.HasIndex(message => new { message.TenantId, message.OwnerUserId, message.ClientMessageId })
            .IsUnique()
            .HasDatabaseName("IX_ConversationMessages_IdempotencyKey")
            .HasFilter("\"ClientMessageId\" IS NOT NULL");
    }
}
