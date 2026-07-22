using Lingban.Domain.Entities.Production;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Lingban.Infrastructure.Data.Configurations;

public class ProductionLineConfiguration : IEntityTypeConfiguration<ProductionLine>
{
    public void Configure(EntityTypeBuilder<ProductionLine> builder)
    {
        builder.Property(line => line.TenantId).HasMaxLength(64).IsRequired();
        builder.Property(line => line.Code).HasMaxLength(64).IsRequired();
        builder.Property(line => line.Name).HasMaxLength(256).IsRequired();

        builder.HasIndex(line => new { line.TenantId, line.Code }).IsUnique();
    }
}

public class WorkstationConfiguration : IEntityTypeConfiguration<Workstation>
{
    public void Configure(EntityTypeBuilder<Workstation> builder)
    {
        builder.Property(station => station.TenantId).HasMaxLength(64).IsRequired();
        builder.Property(station => station.Code).HasMaxLength(64).IsRequired();
        builder.Property(station => station.Name).HasMaxLength(256).IsRequired();

        builder.HasIndex(station => new { station.TenantId, station.Code }).IsUnique();
    }
}

public class ProcessRouteConfiguration : IEntityTypeConfiguration<ProcessRoute>
{
    public void Configure(EntityTypeBuilder<ProcessRoute> builder)
    {
        builder.Property(route => route.TenantId).HasMaxLength(64).IsRequired();
        builder.Property(route => route.Name).HasMaxLength(256).IsRequired();

        builder.HasOne(route => route.Product)
            .WithMany()
            .HasForeignKey(route => route.ProductId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public class ProcessStepConfiguration : IEntityTypeConfiguration<ProcessStep>
{
    public void Configure(EntityTypeBuilder<ProcessStep> builder)
    {
        builder.Property(step => step.TenantId).HasMaxLength(64).IsRequired();
        builder.Property(step => step.Name).HasMaxLength(256).IsRequired();

        builder.HasOne(step => step.Workstation)
            .WithMany()
            .HasForeignKey(step => step.WorkstationId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}

public class WorkOrderConfiguration : IEntityTypeConfiguration<WorkOrder>
{
    public void Configure(EntityTypeBuilder<WorkOrder> builder)
    {
        builder.Property(order => order.TenantId).HasMaxLength(64).IsRequired();
        builder.Property(order => order.Code).HasMaxLength(64).IsRequired();
        builder.Property(order => order.UnitOfMeasure).HasMaxLength(16).IsRequired();

        builder.HasIndex(order => new { order.TenantId, order.Code }).IsUnique();
        builder.HasIndex(order => new { order.TenantId, order.Status });

        builder.HasOne(order => order.Product)
            .WithMany()
            .HasForeignKey(order => order.ProductId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(order => order.ProductionLine)
            .WithMany()
            .HasForeignKey(order => order.ProductionLineId)
            .OnDelete(DeleteBehavior.Restrict);

        // 消耗集合走私有字段,保证只能经 RecordConsumption 记账。
        builder.HasMany(order => order.Consumptions)
            .WithOne(consumption => consumption.WorkOrder)
            .HasForeignKey(consumption => consumption.WorkOrderId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Navigation(order => order.Consumptions)
            .UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

public class WorkOrderOperationConfiguration : IEntityTypeConfiguration<WorkOrderOperation>
{
    public void Configure(EntityTypeBuilder<WorkOrderOperation> builder)
    {
        builder.Property(operation => operation.TenantId).HasMaxLength(64).IsRequired();
        builder.Property(operation => operation.Name).HasMaxLength(256).IsRequired();

        builder.HasOne(operation => operation.WorkOrder)
            .WithMany(order => order.Operations)
            .HasForeignKey(operation => operation.WorkOrderId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(operation => operation.Workstation)
            .WithMany()
            .HasForeignKey(operation => operation.WorkstationId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
