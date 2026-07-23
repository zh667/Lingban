using Lingban.Domain.Entities.Knowledge;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pgvector;

namespace Lingban.Infrastructure.Data.Configurations;

public class KnowledgeDocumentConfiguration : IEntityTypeConfiguration<KnowledgeDocument>
{
    public void Configure(EntityTypeBuilder<KnowledgeDocument> builder)
    {
        builder.Property(document => document.TenantId).HasMaxLength(64).IsRequired();
        builder.Property(document => document.Title).HasMaxLength(256).IsRequired();
        builder.Property(document => document.SourceFileName).HasMaxLength(256).IsRequired();
        builder.HasIndex(document => new { document.TenantId, document.SourceFileName }).IsUnique();
        builder.HasAlternateKey(document => new { document.TenantId, document.Id });
    }
}

public class KnowledgeChunkConfiguration : IEntityTypeConfiguration<KnowledgeChunk>
{
    public void Configure(EntityTypeBuilder<KnowledgeChunk> builder)
    {
        builder.Property(chunk => chunk.TenantId).HasMaxLength(64).IsRequired();
        builder.Property(chunk => chunk.Section).HasMaxLength(512);
        builder.Property(chunk => chunk.Text).IsRequired();

        // 向量为 shadow 属性:bge-m3 1024 维;Domain 不引用 pgvector。
        builder.Property<Vector>("Embedding").HasColumnType("vector(1024)").IsRequired(false);

        builder.HasOne(chunk => chunk.Document)
            .WithMany(document => document.Chunks)
            .HasForeignKey(chunk => new { chunk.TenantId, chunk.DocumentId })
            .HasPrincipalKey(document => new { document.TenantId, document.Id })
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(chunk => new { chunk.TenantId, chunk.DocumentId });
    }
}
