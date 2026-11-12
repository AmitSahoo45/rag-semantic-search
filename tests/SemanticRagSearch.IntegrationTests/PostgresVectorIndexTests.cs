using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Pgvector;
using SemanticRagSearch.Api.Data;
using SemanticRagSearch.Api.Domain;
using SemanticRagSearch.Api.VectorStore;

namespace SemanticRagSearch.IntegrationTests;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Postgres")]
public sealed class PostgresVectorIndexTests(PostgresFixture postgres)
{
    private const string PreviousMigration = "20260919121144_AddTenantApiKeyHash";
    private const string IndexMigration = "20261001184849_AddChunkEmbeddingHnswIndex";

    [PostgresFact]
    public async Task Migration_WhenPgvectorPredatesIterativeScanning_RejectsUpgradeBeforeCreatingIndex()
    {
        await using AppDbContext context = postgres.CreateDbContext();
        IMigrator migrator = context.GetService<IMigrator>();
        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync();
        await using var versionCommand = new NpgsqlCommand(
            "SELECT extversion FROM pg_extension WHERE extname = 'vector'", connection);
        string originalVersion = Assert.IsType<string>(await versionCommand.ExecuteScalarAsync());
        await migrator.MigrateAsync(PreviousMigration);

        try
        {
            // Exercise the migration's version guard without downloading an old image.
            // This fixture owns a disposable database; always restore its catalog below.
            await context.Database.ExecuteSqlRawAsync(
                "UPDATE pg_extension SET extversion = '0.7.4' WHERE extname = 'vector'");

            PostgresException error = await Assert.ThrowsAsync<PostgresException>(() =>
                context.Database.MigrateAsync());

            Assert.Equal(PostgresErrorCodes.FeatureNotSupported, error.SqlState);
            Assert.Contains("pgvector 0.8.0 or later", error.MessageText);
            Assert.DoesNotContain(IndexMigration, await context.Database.GetAppliedMigrationsAsync());
            await using var indexCommand = new NpgsqlCommand(
                "SELECT to_regclass('public.\"IX_chunks_embedding\"')::text", connection);
            Assert.Equal(DBNull.Value, await indexCommand.ExecuteScalarAsync());
        }
        finally
        {
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE pg_extension SET extversion = {originalVersion} WHERE extname = 'vector'");
            await context.Database.MigrateAsync();
        }
    }

    [PostgresFact]
    public async Task Migration_UpgradesPopulatedDatabase_AndRollbackPreservesVectors()
    {
        await using var context = postgres.CreateDbContext();
        IMigrator migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(PreviousMigration);
        try
        {
            Document document = AddDocument(context, "index-upgrade", 3, 0.4);
            Document unrelated = AddDocument(context, "index-unrelated", 1, 1.2);
            // The historical schema predates the generated full-text column.
            // Seed through the raw store, which writes only source columns.
            Chunk[] seeded = context.ChangeTracker.Entries<Chunk>().Select(entry => entry.Entity).ToArray();
            foreach (var entry in context.ChangeTracker.Entries<Chunk>().ToArray())
                entry.State = EntityState.Detached;
            await context.SaveChangesAsync();
            var sourceBuilder = new NpgsqlDataSourceBuilder(postgres.ConnectionString);
            sourceBuilder.UseVector();
            await using NpgsqlDataSource source = sourceBuilder.Build();
            var store = new PgVectorStoreService(source, NullLogger<PgVectorStoreService>.Instance);
            await store.StoreBatchAsync(seeded);
            Chunk[] original = await context.Chunks.AsNoTracking()
                .Where(chunk => chunk.DocumentId == document.DocumentId || chunk.DocumentId == unrelated.DocumentId)
                .OrderBy(chunk => chunk.ChunkId)
                .Select(chunk => new Chunk { ChunkId = chunk.ChunkId, TextContent = chunk.TextContent, Embedding = chunk.Embedding })
                .ToArrayAsync();
            await context.Database.MigrateAsync();
            Assert.False(context.Database.HasPendingModelChanges());

            await using var connection = new NpgsqlConnection(postgres.ConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand("""
                SELECT indexdef FROM pg_indexes
                WHERE schemaname = 'public' AND tablename = 'chunks'
                  AND indexname = 'IX_chunks_embedding';
                """, connection);
            string definition = Assert.IsType<string>(await command.ExecuteScalarAsync());
            Assert.Contains("USING hnsw (embedding vector_cosine_ops)", definition);

            await migrator.MigrateAsync(PreviousMigration);
            Assert.Null(await command.ExecuteScalarAsync());
            Chunk[] preserved = await context.Chunks.AsNoTracking()
                .Where(chunk => chunk.DocumentId == document.DocumentId || chunk.DocumentId == unrelated.DocumentId)
                .OrderBy(chunk => chunk.ChunkId)
                .Select(chunk => new Chunk { ChunkId = chunk.ChunkId, TextContent = chunk.TextContent, Embedding = chunk.Embedding })
                .ToArrayAsync();
            Assert.Equal(4, preserved.Length);
            Assert.Equal(original.Select(chunk => chunk.ChunkId), preserved.Select(chunk => chunk.ChunkId));
            for (int index = 0; index < original.Length; index++)
            {
                Assert.Equal(original[index].Embedding.ToArray(), preserved[index].Embedding.ToArray());
                Assert.Equal(original[index].TextContent, preserved[index].TextContent);
            }
            Assert.True(await context.Documents.AnyAsync(item => item.DocumentId == document.DocumentId));
            Assert.True(await context.Documents.AnyAsync(item => item.DocumentId == unrelated.DocumentId));
            Assert.True(await context.Tenants.AnyAsync(item => item.TenantId == unrelated.TenantId));
        }
        finally
        {
            await context.Database.MigrateAsync();
        }
    }

    [PostgresFact]
    public async Task Search_UsesHnswAndContinuesPastOtherTenants_WithoutLosingOrderOrThreshold()
    {
        await using var context = postgres.CreateDbContext();
        Document target = AddDocument(context, "index-target", 12, 0.4);
        AddDocument(context, "index-competing", 160, 0.001, 0.001);
        await context.SaveChangesAsync();
        // Model the populated index built by the migration. This fixture verifies
        // filtered ANN behavior, not perfect recall for every incremental graph.
        await context.Database.ExecuteSqlRawAsync("REINDEX INDEX \"IX_chunks_embedding\"");

        // Force an ANN path even for this small fixture. More competing vectors than
        // the default candidate budget expose tenant starvation without iterative scans.
        var settings = new NpgsqlConnectionStringBuilder(postgres.ConnectionString)
        {
            Options = "-c enable_seqscan=off -c enable_bitmapscan=off -c enable_sort=off -c hnsw.ef_search=40",
            MaxPoolSize = 1,
            NoResetOnClose = true
        };
        var builder = new NpgsqlDataSourceBuilder(settings.ConnectionString);
        builder.UseVector();
        await using NpgsqlDataSource source = builder.Build();
        Vector query = CreateVector(0);

        await using (NpgsqlConnection connection = await source.OpenConnectionAsync())
        {
            const string unconfiguredSearch = """
                SELECT chunk_id, document_id, tenant_id, chunk_index, text_content,
                    token_count, embedding, metadata, created_at,
                    1 - (embedding <=> @query) AS score
                FROM chunks
                WHERE tenant_id = @tenant AND 1 - (embedding <=> @query) >= 0.8
                ORDER BY embedding <=> @query LIMIT 3;
                """;
            await using var explain = new NpgsqlCommand("EXPLAIN (FORMAT TEXT) " + unconfiguredSearch, connection);
            explain.Parameters.AddWithValue("tenant", target.TenantId);
            explain.Parameters.AddWithValue("query", query);
            var plan = new List<string>();
            await using (NpgsqlDataReader reader = await explain.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                    plan.Add(reader.GetString(0));
            }
            Assert.Contains(plan, line => line.Contains("Index Scan using \"IX_chunks_embedding\"", StringComparison.Ordinal));

            // Prove the initial candidate budget is consumed by the competing tenant.
            await using var control = new NpgsqlCommand(unconfiguredSearch, connection);
            control.Parameters.AddWithValue("tenant", target.TenantId);
            control.Parameters.AddWithValue("query", query);
            await using NpgsqlDataReader controlReader = await control.ExecuteReaderAsync();
            Assert.False(await controlReader.ReadAsync());
        }

        var store = new PgVectorStoreService(source, NullLogger<PgVectorStoreService>.Instance);
        IReadOnlyList<SimilarityResult> results = await store.SearchSimilarAsync(target.TenantId, query, 3, 0.8);
        Assert.Equal(3, results.Count);
        // ANN can omit exact nearest neighbors; the returned matches must still
        // satisfy tenant, top-K, score ordering, and threshold contracts.
        Assert.All(results, result =>
        {
            Assert.Equal(target.TenantId, result.Chunk.TenantId);
            Assert.InRange(result.Score, 0.8, 1.0);
        });
        Assert.True(results[0].Score > results[1].Score && results[1].Score > results[2].Score);

        Assert.Empty(await store.SearchSimilarAsync(target.TenantId, query, 3, 0.99));

        // Disabling Npgsql's automatic reset proves the transaction itself restores tuning.
        await using NpgsqlConnection reused = await source.OpenConnectionAsync();
        await using var setting = new NpgsqlCommand("SHOW hnsw.iterative_scan", reused);
        Assert.Equal("off", await setting.ExecuteScalarAsync());
    }

    private static Document AddDocument(AppDbContext context,
        string label, int count, double initialAngle, double step = 0.03)
    {
        var tenant = new Tenant
        {
            TenantName = label,
            TenantDescription = "Isolated HNSW regression fixture",
            Email = $"{Guid.NewGuid():N}@example.test"
        };
        var document = new Document
        {
            TenantId = tenant.TenantId,
            Title = label,
            ContentHash = Guid.NewGuid().ToString("N")
        };
        context.Tenants.Add(tenant);
        context.Documents.Add(document);
        for (int index = 0; index < count; index++)
        {
            context.Chunks.Add(new Chunk
            {
                DocumentId = document.DocumentId,
                TenantId = tenant.TenantId,
                ChunkIndex = index,
                TextContent = $"{label} chunk {index}",
                TokenCount = 4,
                Embedding = CreateVector(initialAngle + index * step),
                MetaData = new ChunkMetaData { ChunkStrategy = "index_test" }
            });
        }
        return document;
    }

    private static Vector CreateVector(double angle)
    {
        var values = new float[768];
        values[766] = (float)Math.Cos(angle);
        values[767] = (float)Math.Sin(angle);
        return new Vector(values);
    }
}
