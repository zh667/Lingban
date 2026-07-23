using Lingban.Domain.Entities.Actions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Lingban.Infrastructure.Data.Configurations;

public class PendingActionConfiguration : IEntityTypeConfiguration<PendingAction>
{
    public void Configure(EntityTypeBuilder<PendingAction> builder)
    {
        builder.Property(action => action.TenantId).HasMaxLength(64).IsRequired();
        builder.Property(action => action.OwnerUserId).HasMaxLength(128).IsRequired();
        builder.Property(action => action.ActionType).HasMaxLength(64).IsRequired();
        builder.Property(action => action.Summary).HasMaxLength(512).IsRequired();
        builder.Property(action => action.PayloadJson).IsRequired();
        builder.HasIndex(action => new { action.TenantId, action.OwnerUserId, action.Status });
    }
}
