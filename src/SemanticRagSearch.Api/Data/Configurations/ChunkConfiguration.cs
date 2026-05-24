using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SemanticRagSearch.Api.Domain;

namespace SemanticRagSearch.Api.Data.Configurations;

public class ChunkConfiguration : IEntityTypeConfiguration<Chunk>
{
    public void Configure(EntityTypeBuilder<Chunk> builder)
    {
        builder.ToTable("chunks");

        builder.HasKey(c => c.ChunkId);

        builder.Property(c => c.ChunkId)
            .HasColumnName("chunk_id");

        builder.Property(c => c.DocumentId)
            .HasColumnName("document_id")
            .IsRequired();

        builder.Property(c => c.TenantId)
            .HasColumnName("tenant_id")
            .IsRequired();

        builder.Property(c => c.ChunkIndex)
            .HasColumnName("chunk_index")
            .IsRequired();

        builder.Property(c => c.TextContent)
            .HasColumnName("text_content")
            .IsRequired();

        builder.Property(c => c.TokenCount)
            .HasColumnName("token_count")
            .IsRequired();

        builder.Property(c => c.Embedding)
            .HasColumnName("embedding")
            .HasColumnType("vector(768)")
            .IsRequired();

        builder.OwnsOne(c => c.MetaData, metadata =>
        {
            metadata.ToJson("metadata");
            metadata.Property(m => m.PageNumber);
            metadata.Property(m => m.SectionTitle).HasMaxLength(300);
            metadata.Property(m => m.ChunkStrategy).HasMaxLength(50);
        });

        builder.Property(c => c.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        builder.HasOne(c => c.Document)
            .WithMany(d => d.Chunks)
            .HasForeignKey(c => c.DocumentId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(c => c.Tenant)
            .WithMany()
            .HasForeignKey(c => c.TenantId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(c => new { c.DocumentId, c.ChunkIndex })
            .IsUnique();

        builder.HasIndex(c => c.TenantId);
    }
}
