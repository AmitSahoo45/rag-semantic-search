using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SemanticRagSearch.Api.Domain;

namespace SemanticRagSearch.Api.Data.Configurations;

public class TenantConfiguration : IEntityTypeConfiguration<Tenant>
{
    public void Configure(EntityTypeBuilder<Tenant> builder)
    {
        builder.ToTable("tenants");

        builder.HasKey(t => t.TenantId);

        builder.Property(t => t.TenantId)
            .HasColumnName("tenant_id");

        builder.Property(t => t.TenantName)
            .HasColumnName("tenant_name")
            .HasMaxLength(200)
            .IsRequired();

        builder.Property(t => t.TenantDescription)
            .HasColumnName("tenant_description")
            .HasMaxLength(1000)
            .IsRequired();

        builder.Property(t => t.Email)
            .HasColumnName("email")
            .HasMaxLength(320)
            .IsRequired();

        builder.HasIndex(t => t.Email)
            .IsUnique();

        builder.Property(t => t.ApiKey)
            .HasColumnName("api_key")
            .HasMaxLength(256);

        builder.Property(t => t.ApiKeyHash)
            .HasColumnName("api_key_hash")
            .HasMaxLength(64);

        builder.HasIndex(t => t.ApiKeyHash).IsUnique();

        builder.Property(t => t.CreatedDate)
            .HasColumnName("created_date")
            .IsRequired();

        builder.Property(t => t.LastModifiedDate)
            .HasColumnName("last_modified_date")
            .IsRequired();

        builder.Property(t => t.IsActive)
            .HasColumnName("is_active")
            .IsRequired();

        DateTime developmentTenantTimestamp =
            new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        builder.HasData(new
        {
            TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001"),
            TenantName = "Development Tenant",
            TenantDescription = "Default tenant for local Phase 1-2 development.",
            Email = "development@semanticragsearch.invalid",
            ApiKey = (string?)null,
            CreatedDate = developmentTenantTimestamp,
            LastModifiedDate = developmentTenantTimestamp,
            IsActive = true
        });
    }
}
