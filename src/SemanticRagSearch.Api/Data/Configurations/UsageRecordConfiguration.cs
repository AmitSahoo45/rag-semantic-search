using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SemanticRagSearch.Api.Domain;

namespace SemanticRagSearch.Api.Data.Configurations;

public class UsageRecordConfiguration : IEntityTypeConfiguration<UsageRecord>
{
    public void Configure(EntityTypeBuilder<UsageRecord> builder)
    {
        builder.ToTable("usage_records");

        builder.HasKey(u => u.UsageRecordId);

        builder.Property(u => u.UsageRecordId)
            .HasColumnName("usage_record_id");

        builder.Property(u => u.TenantId)
            .HasColumnName("tenant_id")
            .IsRequired();

        builder.Property(u => u.Operation)
            .HasColumnName("operation")
            .HasConversion<string>()
            .HasMaxLength(50)
            .IsRequired();

        builder.Property(u => u.InputTokens)
            .HasColumnName("input_tokens")
            .IsRequired();

        builder.Property(u => u.OutputTokens)
            .HasColumnName("output_tokens")
            .IsRequired();

        builder.Property(u => u.LatencyMs)
            .HasColumnName("latency_ms")
            .IsRequired();

        builder.Property(u => u.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        builder.HasOne(u => u.Tenant)
            .WithMany(t => t.UsageRecords)
            .HasForeignKey(u => u.TenantId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(u => new { u.TenantId, u.CreatedAt });
    }
}
