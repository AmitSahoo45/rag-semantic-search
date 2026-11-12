using Microsoft.EntityFrameworkCore;
using SemanticRagSearch.Api.Domain;

namespace SemanticRagSearch.Api.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Tenant> Tenants { get; set; } = null!;
    public DbSet<Document> Documents { get; set; } = null!;
    public DbSet<Chunk> Chunks { get; set; } = null!;
    public DbSet<UsageRecord> UsageRecords { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasPostgresExtension("vector");
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);

        // The generated text index belongs to PostgreSQL. Keep provider-neutral
        // service tests from needing to map Npgsql's shadow tsvector type.
        if (!Database.IsNpgsql())
            modelBuilder.Entity<Chunk>().Ignore("SearchVector");

        base.OnModelCreating(modelBuilder);
    }
}
