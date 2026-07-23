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
            .HasForeignKey(message => message.ConversationId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(message => new { message.TenantId, message.ConversationId });
    }
}
