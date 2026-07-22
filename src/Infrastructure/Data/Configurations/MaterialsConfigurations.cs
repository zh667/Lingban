using Lingban.Domain.Entities.Materials;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Lingban.Infrastructure.Data.Configurations;

public class ProductConfiguration : IEntityTypeConfiguration<Product>
{
    public void Configure(EntityTypeBuilder<Product> builder)
    {
        builder.Property(product => product.TenantId).HasMaxLength(64).IsRequired();
        builder.Property(product => product.Code).HasMaxLength(64).IsRequired();
        builder.Property(product => product.Name).HasMaxLength(256).IsRequired();
        builder.Property(product => product.UnitOfMeasure).HasMaxLength(16).IsRequired();

        builder.HasIndex(product => new { product.TenantId, product.Code }).IsUnique();
    }
}

public class BomLineConfiguration : IEntityTypeConfiguration<BomLine>
{
    public void Configure(EntityTypeBuilder<BomLine> builder)
    {
        builder.Property(line => line.TenantId).HasMaxLength(64).IsRequired();
        builder.Property(line => line.UnitOfMeasure).HasMaxLength(16).IsRequired();

        builder.HasOne(line => line.Product)
            .WithMany(product => product.BomLines)
            .HasForeignKey(line => line.ProductId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(line => line.ComponentProduct)
            .WithMany()
            .HasForeignKey(line => line.ComponentProductId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(line => new { line.TenantId, line.ProductId, line.ComponentProductId }).IsUnique();
    }
}

public class MaterialLotConfiguration : IEntityTypeConfiguration<MaterialLot>
{
    public void Configure(EntityTypeBuilder<MaterialLot> builder)
    {
        builder.Property(lot => lot.TenantId).HasMaxLength(64).IsRequired();
        builder.Property(lot => lot.LotNumber).HasMaxLength(64).IsRequired();
        builder.Property(lot => lot.UnitOfMeasure).HasMaxLength(16).IsRequired();
        builder.Property(lot => lot.SupplierLotNumber).HasMaxLength(64);

        builder.HasIndex(lot => new { lot.TenantId, lot.LotNumber }).IsUnique();

        builder.HasOne(lot => lot.Product)
            .WithMany()
            .HasForeignKey(lot => lot.ProductId)
            .OnDelete(DeleteBehavior.Restrict);

        // 谱系"向上"边:产出工单删不掉已有产出批次。
        builder.HasOne(lot => lot.ProducedByWorkOrder)
            .WithMany(order => order.OutputLots)
            .HasForeignKey(lot => lot.ProducedByWorkOrderId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public class MaterialConsumptionConfiguration : IEntityTypeConfiguration<MaterialConsumption>
{
    public void Configure(EntityTypeBuilder<MaterialConsumption> builder)
    {
        builder.Property(consumption => consumption.TenantId).HasMaxLength(64).IsRequired();
        builder.Property(consumption => consumption.UnitOfMeasure).HasMaxLength(16).IsRequired();
        builder.Property(consumption => consumption.RecordedBy).HasMaxLength(128);

        // 谱系边不可级联清除:批次与工位被引用时禁止删除。
        builder.HasOne(consumption => consumption.MaterialLot)
            .WithMany(lot => lot.Consumptions)
            .HasForeignKey(consumption => consumption.MaterialLotId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(consumption => consumption.Workstation)
            .WithMany()
            .HasForeignKey(consumption => consumption.WorkstationId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(consumption => new { consumption.TenantId, consumption.MaterialLotId });
        builder.HasIndex(consumption => new { consumption.TenantId, consumption.WorkOrderId });
    }
}
