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
        builder.HasAlternateKey(product => new { product.TenantId, product.Id });
    }
}

public class BomLineConfiguration : IEntityTypeConfiguration<BomLine>
{
    public void Configure(EntityTypeBuilder<BomLine> builder)
    {
        builder.Property(line => line.TenantId).HasMaxLength(64).IsRequired();
        builder.Property(line => line.UnitOfMeasure).HasMaxLength(16).IsRequired();

        // 复合租户外键(M4 债):数据库层面杜绝跨租户引用。
        builder.HasOne(line => line.Product)
            .WithMany(product => product.BomLines)
            .HasForeignKey(line => new { line.TenantId, line.ProductId })
            .HasPrincipalKey(product => new { product.TenantId, product.Id })
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(line => line.ComponentProduct)
            .WithMany()
            .HasForeignKey(line => new { line.TenantId, line.ComponentProductId })
            .HasPrincipalKey(product => new { product.TenantId, product.Id })
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

        // PostgreSQL xmin 乐观并发:并发扣减同一批次时后写方冲突重试,杜绝超卖。
        builder.Property<uint>("xmin")
            .HasColumnName("xmin")
            .HasColumnType("xid")
            .ValueGeneratedOnAddOrUpdate()
            .IsConcurrencyToken();

        builder.HasAlternateKey(lot => new { lot.TenantId, lot.Id });

        builder.HasOne(lot => lot.Product)
            .WithMany()
            .HasForeignKey(lot => new { lot.TenantId, lot.ProductId })
            .HasPrincipalKey(product => new { product.TenantId, product.Id })
            .OnDelete(DeleteBehavior.Restrict);

        // 谱系"向上"边:产出工单删不掉已有产出批次。
        builder.HasOne(lot => lot.ProducedByWorkOrder)
            .WithMany(order => order.OutputLots)
            .HasForeignKey(lot => new { lot.TenantId, lot.ProducedByWorkOrderId })
            .HasPrincipalKey(order => new { order.TenantId, order.Id })
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
            .HasForeignKey(consumption => new { consumption.TenantId, consumption.MaterialLotId })
            .HasPrincipalKey(lot => new { lot.TenantId, lot.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(consumption => consumption.Workstation)
            .WithMany()
            .HasForeignKey(consumption => new { consumption.TenantId, consumption.WorkstationId })
            .HasPrincipalKey(station => new { station.TenantId, station.Id })
            .OnDelete(DeleteBehavior.Restrict);

        // 幂等兜底:同租户下同一事件键只允许一条消耗记录。
        builder.HasIndex(consumption => new { consumption.TenantId, consumption.EventId })
            .IsUnique()
            .HasFilter("\"EventId\" IS NOT NULL");

        builder.HasIndex(consumption => new { consumption.TenantId, consumption.MaterialLotId });
        builder.HasIndex(consumption => new { consumption.TenantId, consumption.WorkOrderId });
    }
}
