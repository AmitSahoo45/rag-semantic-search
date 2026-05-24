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

        base.OnModelCreating(modelBuilder);
    }
}
