using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SemanticRagSearch.Api.Domain;

namespace SemanticRagSearch.Api.Data.Configurations;

public class DocumentConfiguration : IEntityTypeConfiguration<Document>
{
    public void Configure(EntityTypeBuilder<Document> builder)
    {
        builder.ToTable("documents");

        builder.HasKey(d => d.DocumentId);

        builder.Property(d => d.DocumentId)
            .HasColumnName("document_id");

        builder.Property(d => d.TenantId)
            .HasColumnName("tenant_id")
            .IsRequired();

        builder.Property(d => d.Title)
            .HasColumnName("title")
            .HasMaxLength(300)
            .IsRequired();

        builder.Property(d => d.SourceUrl)
            .HasColumnName("source_url")
            .HasMaxLength(2048);

        builder.Property(d => d.ContentHash)
            .HasColumnName("content_hash")
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(d => d.IngestedAt)
            .HasColumnName("ingested_at")
            .IsRequired();

        builder.HasOne(d => d.Tenant)
            .WithMany(t => t.Documents)
            .HasForeignKey(d => d.TenantId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(d => new { d.TenantId, d.ContentHash })
            .IsUnique();
    }
}
