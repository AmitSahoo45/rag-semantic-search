using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Pgvector.EntityFrameworkCore;
using SemanticRagSearch.Api.Data;

namespace SemanticRagSearch.UnitTests;

public class VectorIndexMigrationTests
{
    [Fact]
    public void Migration_AddsCosineHnswIndex_AndRollbackDropsOnlyThatIndex()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=unused;Username=unused;Password=unused",
                postgres => postgres.UseVector())
            .Options;
        using var context = new AppDbContext(options);
        IMigrationsAssembly migrations = context.GetService<IMigrationsAssembly>();
        var entry = Assert.Single(migrations.Migrations,
            item => item.Key.EndsWith("_AddChunkEmbeddingHnswIndex", StringComparison.Ordinal));
        Migration migration = migrations.CreateMigration(entry.Value, context.Database.ProviderName!);

        CreateIndexOperation index = Assert.Single(migration.UpOperations.OfType<CreateIndexOperation>());
        Assert.Equal("chunks", index.Table);
        Assert.Equal(["embedding"], index.Columns);
        Assert.Equal("hnsw", index["Npgsql:IndexMethod"]);
        Assert.Equal(new[] { "vector_cosine_ops" }, Assert.IsType<string[]>(index["Npgsql:IndexOperators"]));
        DropIndexOperation rollback = Assert.IsType<DropIndexOperation>(Assert.Single(migration.DownOperations));
        Assert.Equal(index.Name, rollback.Name);
        Assert.Equal("chunks", rollback.Table);
        Assert.False(context.Database.HasPendingModelChanges());
    }
}
