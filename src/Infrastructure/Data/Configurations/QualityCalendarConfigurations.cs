using Lingban.Domain.Entities.Calendar;
using Lingban.Domain.Entities.Quality;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Lingban.Infrastructure.Data.Configurations;

public class DefectTypeConfiguration : IEntityTypeConfiguration<DefectType>
{
    public void Configure(EntityTypeBuilder<DefectType> builder)
    {
        builder.Property(type => type.TenantId).HasMaxLength(64).IsRequired();
        builder.Property(type => type.Code).HasMaxLength(64).IsRequired();
        builder.Property(type => type.Name).HasMaxLength(256).IsRequired();

        builder.HasIndex(type => new { type.TenantId, type.Code }).IsUnique();
    }
}

public class DefectRecordConfiguration : IEntityTypeConfiguration<DefectRecord>
{
    public void Configure(EntityTypeBuilder<DefectRecord> builder)
    {
        builder.Property(record => record.TenantId).HasMaxLength(64).IsRequired();
        builder.Property(record => record.Notes).HasMaxLength(1024);

        builder.HasOne(record => record.DefectType)
            .WithMany()
            .HasForeignKey(record => record.DefectTypeId)
            .OnDelete(DeleteBehavior.Restrict);

        // 质量记录是审计证据,禁止随主数据级联清除。
        builder.HasOne(record => record.WorkOrder)
            .WithMany()
            .HasForeignKey(record => record.WorkOrderId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public class QualityInspectionConfiguration : IEntityTypeConfiguration<QualityInspection>
{
    public void Configure(EntityTypeBuilder<QualityInspection> builder)
    {
        builder.Property(inspection => inspection.TenantId).HasMaxLength(64).IsRequired();
        builder.Property(inspection => inspection.Notes).HasMaxLength(1024);

        builder.HasOne(inspection => inspection.WorkOrder)
            .WithMany()
            .HasForeignKey(inspection => inspection.WorkOrderId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(inspection => inspection.MaterialLot)
            .WithMany()
            .HasForeignKey(inspection => inspection.MaterialLotId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public class ShiftConfiguration : IEntityTypeConfiguration<Shift>
{
    public void Configure(EntityTypeBuilder<Shift> builder)
    {
        builder.Property(shift => shift.TenantId).HasMaxLength(64).IsRequired();
        builder.Property(shift => shift.Code).HasMaxLength(64).IsRequired();
        builder.Property(shift => shift.Name).HasMaxLength(256).IsRequired();

        builder.HasIndex(shift => new { shift.TenantId, shift.Code }).IsUnique();

        builder.Ignore(shift => shift.CrossesMidnight);
    }
}
