using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Pgvector;
using SemanticRagSearch.Api.Data;
using SemanticRagSearch.Api.Domain;
using SemanticRagSearch.Api.Search;
using SemanticRagSearch.Api.VectorStore;

namespace SemanticRagSearch.IntegrationTests;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Postgres")]
public sealed class PostgresFullTextSearchTests(PostgresFixture postgres)
{
    [PostgresFact]
    public async Task Migration_BackfillsGeneratedSearchAndRollbackPreservesSourceAndHnsw()
    {
        await using AppDbContext db = postgres.CreateDbContext();
        IMigrator migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20261001184849_AddChunkEmbeddingHnswIndex");
        try
        {
            Document document = await AddDocumentAsync(db, "migration lexical", "migrationmarker", historicalSchema: true);
            await db.Database.MigrateAsync();
            db.ChangeTracker.Clear();
            Assert.False(db.Database.HasPendingModelChanges());
            using var source = CreateSource();
            var lexical = new PgFullTextSearchService(source);
            Assert.Single(await lexical.SearchAsync(document.TenantId, "migrationmarker", QueryVector(), 5, 0.5));
            await using var connection = await source.OpenConnectionAsync();
            await using var index = new NpgsqlCommand("SELECT indexdef FROM pg_indexes WHERE indexname = 'IX_chunks_search_vector'", connection);
            Assert.Contains("USING gin (search_vector)", Assert.IsType<string>(await index.ExecuteScalarAsync()));
            await migrator.MigrateAsync("20261001184849_AddChunkEmbeddingHnswIndex");
            Assert.Null(await index.ExecuteScalarAsync());
            await using var preserved = new NpgsqlCommand("SELECT text_content FROM chunks WHERE document_id = @id", connection);
            preserved.Parameters.AddWithValue("id", document.DocumentId);
            Assert.Equal("migrationmarker", await preserved.ExecuteScalarAsync());
            await using var hnsw = new NpgsqlCommand("SELECT to_regclass('public.\"IX_chunks_embedding\"')::text", connection);
            Assert.Equal("\"IX_chunks_embedding\"", await hnsw.ExecuteScalarAsync());
        }
        finally { await db.Database.MigrateAsync(); }
    }

    [PostgresFact]
    public async Task Search_EnforcesTenantThresholdAndRefreshesAfterRawUpsert()
    {
        await using AppDbContext db = postgres.CreateDbContext();
        Document target = await AddDocumentAsync(db, "lexical target", "refresh token expiration");
        await AddDocumentAsync(db, "lexical other", "refresh token expiration");
        Document distant = await AddDocumentAsync(db, "lexical distant", "refresh token expiration", target.TenantId, false);
        await using var source = CreateSource();
        var lexical = new PgFullTextSearchService(source);
        SimilarityResult result = Assert.Single(await lexical.SearchAsync(target.TenantId, "refresh token", QueryVector(), 5, 0.5));
        Assert.Equal(target.DocumentId, result.Chunk.DocumentId);
        Assert.InRange(result.Score, 0.999, 1.0);
        Assert.DoesNotContain(await lexical.SearchAsync(target.TenantId, "refresh token", QueryVector(), 5, 0.5),
            match => match.Chunk.DocumentId == distant.DocumentId);
        Chunk chunk = await db.Chunks.SingleAsync(chunk => chunk.DocumentId == target.DocumentId);
        chunk.TextContent = "backupmarker restoration";
        var store = new PgVectorStoreService(source, NullLogger<PgVectorStoreService>.Instance);
        await store.StoreAsync(chunk);
        Assert.Empty(await lexical.SearchAsync(target.TenantId, "refresh token", QueryVector(), 5, 0.5));
        Assert.Single(await lexical.SearchAsync(target.TenantId, "backupmarker", QueryVector(), 1, 0.5));
        Assert.Empty(await lexical.SearchAsync(target.TenantId, "the and", QueryVector(), 5, 0.5));
        Assert.Empty(await lexical.SearchAsync(target.TenantId, "' ; DROP TABLE chunks; --", QueryVector(), 5, 0.5));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lexical.SearchAsync(target.TenantId,
            "backupmarker", QueryVector(), 5, 0.5, cancellation.Token));
    }

    private NpgsqlDataSource CreateSource()
    {
        var builder = new NpgsqlDataSourceBuilder(postgres.ConnectionString);
        builder.UseVector();
        return builder.Build();
    }

    private static Vector QueryVector(bool matching = true)
    {
        var values = new float[768];
        values[matching ? 0 : 1] = 1;
        return new Vector(values);
    }

    private async Task<Document> AddDocumentAsync(AppDbContext db, string title, string text,
        Guid? tenantId = null, bool matching = true, bool historicalSchema = false)
    {
        if (tenantId is null)
        {
            var tenant = new Tenant { TenantName = title, TenantDescription = title, Email = $"{Guid.NewGuid():N}@example.test" };
            db.Tenants.Add(tenant);
            tenantId = tenant.TenantId;
        }
        var document = new Document { TenantId = tenantId.Value, Title = title, ContentHash = Guid.NewGuid().ToString("N") };
        db.Documents.Add(document);
        var chunk = new Chunk { DocumentId = document.DocumentId, TenantId = document.TenantId,
            TextContent = text, Embedding = QueryVector(matching), TokenCount = 4 };
        if (!historicalSchema) db.Chunks.Add(chunk);
        await db.SaveChangesAsync();
        if (historicalSchema)
        {
            await using NpgsqlDataSource source = CreateSource();
            await new PgVectorStoreService(source, NullLogger<PgVectorStoreService>.Instance).StoreAsync(chunk);
        }
        return document;
    }
}
