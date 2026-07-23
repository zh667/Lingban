using Lingban.Domain.Entities.Equipment;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Lingban.Infrastructure.Data.Configurations;

public class EquipmentConfiguration : IEntityTypeConfiguration<Equipment>
{
    public void Configure(EntityTypeBuilder<Equipment> builder)
    {
        builder.Property(equipment => equipment.TenantId).HasMaxLength(64).IsRequired();
        builder.Property(equipment => equipment.Code).HasMaxLength(64).IsRequired();
        builder.Property(equipment => equipment.Name).HasMaxLength(256).IsRequired();
        builder.Property(equipment => equipment.Model).HasMaxLength(128);

        builder.HasIndex(equipment => new { equipment.TenantId, equipment.Code }).IsUnique();
        builder.HasAlternateKey(equipment => new { equipment.TenantId, equipment.Id });

        builder.HasOne(equipment => equipment.Workstation)
            .WithMany()
            .HasForeignKey(equipment => equipment.WorkstationId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}

public class EquipmentStatusRecordConfiguration : IEntityTypeConfiguration<EquipmentStatusRecord>
{
    public void Configure(EntityTypeBuilder<EquipmentStatusRecord> builder)
    {
        builder.Property(record => record.TenantId).HasMaxLength(64).IsRequired();
        builder.Property(record => record.Remarks).HasMaxLength(512);

        builder.HasOne(record => record.Equipment)
            .WithMany()
            .HasForeignKey(record => new { record.TenantId, record.EquipmentId })
            .HasPrincipalKey(equipment => new { equipment.TenantId, equipment.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(record => new { record.TenantId, record.EquipmentId, record.StartUtc });

        builder.ToTable(table =>
        {
            table.HasCheckConstraint("CK_EquipmentStatus_EndAfterStart", "\"EndUtc\" IS NULL OR \"EndUtc\" > \"StartUtc\"");
            table.HasCheckConstraint("CK_EquipmentStatus_SourceSpecified", "\"Source\" <> 0");
        });
    }
}

public class DowntimeRecordConfiguration : IEntityTypeConfiguration<DowntimeRecord>
{
    public void Configure(EntityTypeBuilder<DowntimeRecord> builder)
    {
        builder.Property(record => record.TenantId).HasMaxLength(64).IsRequired();
        builder.Property(record => record.Reason).HasMaxLength(256).IsRequired();
        builder.Property(record => record.Description).HasMaxLength(1024);

        builder.HasOne(record => record.Equipment)
            .WithMany()
            .HasForeignKey(record => new { record.TenantId, record.EquipmentId })
            .HasPrincipalKey(equipment => new { equipment.TenantId, equipment.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(record => new { record.TenantId, record.EquipmentId, record.StartUtc });

        builder.ToTable(table =>
        {
            table.HasCheckConstraint("CK_Downtime_EndAfterStart", "\"EndUtc\" IS NULL OR \"EndUtc\" > \"StartUtc\"");
            table.HasCheckConstraint("CK_Downtime_SourceSpecified", "\"Source\" <> 0");
        });
    }
}
