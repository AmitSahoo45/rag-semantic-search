using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Npgsql;
using Pgvector;
using SemanticRagSearch.Api.Data;
using SemanticRagSearch.Api.Domain;
using SemanticRagSearch.Api.Embedding;
using SemanticRagSearch.Api.Ingestion;
using SemanticRagSearch.Api.Search;
using SemanticRagSearch.Api.VectorStore;

namespace SemanticRagSearch.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class PostgresPersistenceApiTests(PostgresFixture postgres)
{
    private const string InitialMigration = "20260525175102_InitialSchema";
    private static readonly Guid DevelopmentTenantId =
        Guid.Parse("00000000-0000-0000-0000-000000000001");

    [PostgresFact]
    [Trait("Category", "Postgres")]
    public async Task HttpHybridSearch_ReturnsFusionScoresAndEnforcesThresholdAndTenant()
    {
        Tenant tenant = await AddTenantAsync("hybrid-http");
        Tenant other = await AddTenantAsync("hybrid-other");
        using var factory = new PostgresApiFactory(postgres.ConnectionString, new ControlledEmbeddingService());
        using HttpClient client = factory.CreateClient();
        DocumentIngestionResult relevant = await IngestAsync(client,
            new(tenant.TenantId, "Authentication", null, "ALPHA refresh token expiration"));
        await IngestAsync(client, new(tenant.TenantId, "Distant lexical", null, "BETA refresh token expiration"));
        await IngestAsync(client, new(other.TenantId, "Other tenant", null, "ALPHA refresh token expiration"));

        SemanticSearchResponse response = await SearchAsync(client,
            new(tenant.TenantId, "refresh token", 2, 0.9, "hybrid"));

        Assert.Equal("hybrid", response.RetrievalMode);
        SemanticSearchMatch match = Assert.Single(response.Matches);
        Assert.Equal(relevant.DocumentId, match.DocumentId);
        Assert.InRange(match.Score, 0.999, 1.0);
        Assert.Equal(2.0 / 61, match.RankingScore!.Value, 12);
    }

    [PostgresFact]
    [Trait("Category", "Postgres")]
    public async Task HttpIngestion_PersistsVectorAndSearchableSource_AndDuplicateIsIdempotent()
    {
        Tenant tenant = await AddTenantAsync("http-roundtrip");
        var embeddings = new ControlledEmbeddingService();
        using var factory = new PostgresApiFactory(postgres.ConnectionString, embeddings);
        using HttpClient client = factory.CreateClient();
        var request = new DocumentIngestionRequest(
            tenant.TenantId,
            "Alpha handbook",
            "https://example.test/alpha",
            "ALPHA retrieval facts for the integration test.");

        using HttpResponseMessage createResponse =
            await client.PostTenantJsonAsync("/api/v1/documents", request);
        DocumentIngestionResult created = await ReadAsync<DocumentIngestionResult>(createResponse);

        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        Assert.False(created.WasDuplicate);
        Assert.True(created.ChunkCount > 0);
        Assert.Equal(768, await ReadVectorDimensionsAsync(created.DocumentId));

        using HttpResponseMessage searchResponse = await client.PostTenantJsonAsync(
            "/api/v1/search",
            new SemanticSearchRequest(tenant.TenantId, "ALPHA query", 5, 0.99));
        SemanticSearchResponse search = await ReadAsync<SemanticSearchResponse>(searchResponse);
        SemanticSearchMatch match = Assert.Single(search.Matches);

        Assert.Equal(HttpStatusCode.OK, searchResponse.StatusCode);
        Assert.Equal(created.DocumentId, match.DocumentId);
        Assert.Equal("Alpha handbook", match.DocumentTitle);
        Assert.Equal("https://example.test/alpha", match.SourceUrl);
        Assert.InRange(match.Score, 0.999, 1.0);

        using HttpResponseMessage duplicateResponse = await client.PostTenantJsonAsync(
            "/api/v1/documents",
            request with { Title = "A changed title is ignored for duplicate content" });
        DocumentIngestionResult duplicate = await ReadAsync<DocumentIngestionResult>(duplicateResponse);

        Assert.Equal(HttpStatusCode.OK, duplicateResponse.StatusCode);
        Assert.True(duplicate.WasDuplicate);
        Assert.Equal(created.DocumentId, duplicate.DocumentId);
        Assert.Equal(created.ChunkCount, duplicate.ChunkCount);

        await using AppDbContext dbContext = postgres.CreateDbContext();
        Assert.Equal(1, await dbContext.Documents.CountAsync(d => d.TenantId == tenant.TenantId));
        Assert.Equal(created.ChunkCount, await dbContext.Chunks.CountAsync(c => c.TenantId == tenant.TenantId));
    }

    [PostgresFact]
    [Trait("Category", "Postgres")]
    public async Task Search_EnforcesTenantTopKThresholdAndSimilarityOrder()
    {
        Tenant tenant = await AddTenantAsync("search-main");
        Tenant otherTenant = await AddTenantAsync("search-other");
        var embeddings = new ControlledEmbeddingService();
        using var factory = new PostgresApiFactory(postgres.ConnectionString, embeddings);
        using HttpClient client = factory.CreateClient();

        DocumentIngestionResult alpha = await IngestAsync(
            client,
            new(tenant.TenantId, "Exact", "https://example.test/exact", "ALPHA document"));
        DocumentIngestionResult diagonal = await IngestAsync(
            client,
            new(tenant.TenantId, "Related", "https://example.test/related", "DIAGONAL document"));
        await IngestAsync(
            client,
            new(tenant.TenantId, "Distant", null, "BETA document"));
        await IngestAsync(
            client,
            new(otherTenant.TenantId, "Other tenant exact", null, "ALPHA other tenant"));

        SemanticSearchResponse topTwo = await SearchAsync(
            client,
            new(tenant.TenantId, "ALPHA query", 2, 0));

        Assert.Equal(2, topTwo.Matches.Count);
        Assert.Equal([alpha.DocumentId, diagonal.DocumentId], topTwo.Matches.Select(m => m.DocumentId));
        Assert.True(topTwo.Matches[0].Score > topTwo.Matches[1].Score);
        Assert.All(topTwo.Matches, match =>
            Assert.DoesNotContain("Other tenant", match.DocumentTitle, StringComparison.Ordinal));

        SemanticSearchResponse strict = await SearchAsync(
            client,
            new(tenant.TenantId, "ALPHA query", 10, 0.9));

        SemanticSearchMatch strictMatch = Assert.Single(strict.Matches);
        Assert.Equal(alpha.DocumentId, strictMatch.DocumentId);
    }

    [PostgresFact]
    [Trait("Category", "Postgres")]
    public async Task HttpIngestion_WhenChunkDimensionIsInvalid_RollsBackDocumentAndChunks()
    {
        Tenant tenant = await AddTenantAsync("ingestion-atomic");
        var embeddings = new ControlledEmbeddingService { BatchDimension = 3 };
        using var factory = new PostgresApiFactory(postgres.ConnectionString, embeddings);
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.PostTenantJsonAsync(
            "/api/v1/documents",
            new DocumentIngestionRequest(
                tenant.TenantId,
                "Invalid vector",
                null,
                "ALPHA content whose chunk cannot fit vector(768)"));
        using JsonDocument error = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(
            "An unexpected server error occurred.",
            error.RootElement.GetProperty("error").GetString());

        await using AppDbContext dbContext = postgres.CreateDbContext();
        Assert.Equal(0, await dbContext.Documents.CountAsync(d => d.TenantId == tenant.TenantId));
        Assert.Equal(0, await dbContext.Chunks.CountAsync(c => c.TenantId == tenant.TenantId));
    }

    [PostgresFact]
    [Trait("Category", "Postgres")]
    public async Task StoreBatch_WhenOneEmbeddingHasWrongDimension_RollsBackEntireBatch()
    {
        Tenant tenant = await AddTenantAsync("atomic-batch");
        Document document = await AddDocumentWithoutChunksAsync(tenant.TenantId, "atomic batch seed");
        var embeddings = new ControlledEmbeddingService();
        using var factory = new PostgresApiFactory(postgres.ConnectionString, embeddings);
        using IServiceScope scope = factory.Services.CreateScope();
        var vectorStore = scope.ServiceProvider.GetRequiredService<IVectorStoreService>();
        Chunk[] chunks =
        [
            CreateChunk(document, 0, ControlledEmbeddingService.CreateVector("ALPHA")),
            CreateChunk(document, 1, [1f, 0.5f, 0.25f])
        ];

        PostgresException exception = await Assert.ThrowsAsync<PostgresException>(() =>
            vectorStore.StoreBatchAsync(chunks));

        Assert.Equal("22000", exception.SqlState);

        await using AppDbContext dbContext = postgres.CreateDbContext();
        Assert.Equal(0, await dbContext.Chunks.CountAsync(c => c.DocumentId == document.DocumentId));
    }

    [PostgresFact]
    [Trait("Category", "Postgres")]
    public async Task Ingestion_RetriesAnExistingDocumentThatHasNoChunks()
    {
        Tenant tenant = await AddTenantAsync("retry-zero");
        const string content = "ALPHA incomplete document content";
        Document incomplete = await AddDocumentWithoutChunksAsync(tenant.TenantId, content);
        var embeddings = new ControlledEmbeddingService();
        using var factory = new PostgresApiFactory(postgres.ConnectionString, embeddings);
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.PostTenantJsonAsync(
            "/api/v1/documents",
            new DocumentIngestionRequest(tenant.TenantId, "Retry", null, content));
        DocumentIngestionResult result = await ReadAsync<DocumentIngestionResult>(response);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(incomplete.DocumentId, result.DocumentId);
        Assert.False(result.WasDuplicate);
        Assert.True(result.ChunkCount > 0);

        await using AppDbContext dbContext = postgres.CreateDbContext();
        Assert.Equal(1, await dbContext.Documents.CountAsync(d => d.TenantId == tenant.TenantId));
        Assert.Equal(result.ChunkCount, await dbContext.Chunks.CountAsync(c => c.DocumentId == incomplete.DocumentId));
    }

    [PostgresFact]
    [Trait("Category", "Postgres")]
    public async Task ConcurrentNewDocumentIngestion_ConvergesOnOneDocumentAndChunkSet()
    {
        Tenant tenant = await AddTenantAsync("race-new");
        var embeddings = new ControlledEmbeddingService();
        embeddings.UseBatchBarrier(2);
        using var factory = new PostgresApiFactory(postgres.ConnectionString, embeddings);
        using HttpClient client = factory.CreateClient();
        var request = new DocumentIngestionRequest(
            tenant.TenantId,
            "Concurrent",
            null,
            "ALPHA content racing through the new-document path");

        DocumentIngestionResult[] results = await IngestConcurrentlyAsync(client, request);

        Assert.Single(results, result => !result.WasDuplicate);
        Assert.Single(results, result => result.WasDuplicate);
        Assert.Single(results.Select(result => result.DocumentId).Distinct());

        await AssertSinglePersistedDocumentAsync(tenant.TenantId, results[0]);
    }

    [PostgresFact]
    [Trait("Category", "Postgres")]
    public async Task ConcurrentZeroChunkRetry_ConvergesOnOneChunkSet()
    {
        Tenant tenant = await AddTenantAsync("race-zero");
        const string content = "ALPHA content racing through the zero-chunk retry path";
        Document incomplete = await AddDocumentWithoutChunksAsync(tenant.TenantId, content);
        var embeddings = new ControlledEmbeddingService();
        embeddings.UseBatchBarrier(2);
        using var factory = new PostgresApiFactory(postgres.ConnectionString, embeddings);
        using HttpClient client = factory.CreateClient();
        var request = new DocumentIngestionRequest(tenant.TenantId, "Retry race", null, content);

        DocumentIngestionResult[] results = await IngestConcurrentlyAsync(client, request);

        Assert.Single(results, result => !result.WasDuplicate);
        Assert.Single(results, result => result.WasDuplicate);
        Assert.All(results, result => Assert.Equal(incomplete.DocumentId, result.DocumentId));

        await AssertSinglePersistedDocumentAsync(tenant.TenantId, results[0]);
    }

    [PostgresFact]
    [Trait("Category", "Postgres")]
    public async Task DevelopmentTenantMigration_UpgradeAndRollbackPreserveUnrelatedRows()
    {
        await using AppDbContext dbContext = postgres.CreateDbContext();
        IMigrator migrator = dbContext.GetService<IMigrator>();
        Tenant unrelated = CreateTenant("migration-unrelated");

        try
        {
            await migrator.MigrateAsync(InitialMigration);
            // This schema predates ApiKeyHash; insert only columns available at that migration.
            await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO tenants (tenant_id, tenant_name, tenant_description, email, created_date, last_modified_date, is_active)
                VALUES ({unrelated.TenantId}, {unrelated.TenantName}, {unrelated.TenantDescription}, {unrelated.Email},
                    {unrelated.CreatedDate}, {unrelated.LastModifiedDate}, {unrelated.IsActive})
                """);

            await dbContext.Database.MigrateAsync();
            dbContext.ChangeTracker.Clear();

            Assert.True(await dbContext.Tenants.AnyAsync(t => t.TenantId == DevelopmentTenantId));
            Assert.True(await dbContext.Tenants.AnyAsync(t => t.TenantId == unrelated.TenantId));

            await migrator.MigrateAsync(InitialMigration);
            dbContext.ChangeTracker.Clear();

            Assert.False(await dbContext.Tenants.AnyAsync(t => t.TenantId == DevelopmentTenantId));
            Assert.True(await dbContext.Tenants.AnyAsync(t => t.TenantId == unrelated.TenantId));
        }
        finally
        {
            await dbContext.Database.MigrateAsync();
        }
    }

    [PostgresFact]
    [Trait("Category", "Postgres")]
    public async Task VectorSearch_PropagatesCancellationBeforeDatabaseWork()
    {
        var embeddings = new ControlledEmbeddingService();
        using var factory = new PostgresApiFactory(postgres.ConnectionString, embeddings);
        using IServiceScope scope = factory.Services.CreateScope();
        var vectorStore = scope.ServiceProvider.GetRequiredService<IVectorStoreService>();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            vectorStore.SearchSimilarAsync(
                Guid.NewGuid(),
                new Vector(ControlledEmbeddingService.CreateVector("ALPHA")),
                1,
                0,
                cancellation.Token));
    }

    private async Task<Tenant> AddTenantAsync(string label)
    {
        Tenant tenant = CreateTenant(label);
        await using AppDbContext dbContext = postgres.CreateDbContext();
        dbContext.Tenants.Add(tenant);
        await dbContext.SaveChangesAsync();
        return tenant;
    }

    private async Task<Document> AddDocumentWithoutChunksAsync(Guid tenantId, string content)
    {
        var document = new Document
        {
            TenantId = tenantId,
            Title = "Incomplete document",
            ContentHash = ComputeHash(content),
            IngestedAt = DateTime.UtcNow
        };
        await using AppDbContext dbContext = postgres.CreateDbContext();
        dbContext.Documents.Add(document);
        await dbContext.SaveChangesAsync();
        return document;
    }

    private async Task<int> ReadVectorDimensionsAsync(Guid documentId)
    {
        await using AppDbContext dbContext = postgres.CreateDbContext();
        return await dbContext.Database
            .SqlQuery<int>($"SELECT vector_dims(embedding)::integer AS \"Value\" FROM chunks WHERE document_id = {documentId} LIMIT 1")
            .SingleAsync();
    }

    private async Task AssertSinglePersistedDocumentAsync(
        Guid tenantId,
        DocumentIngestionResult result)
    {
        await using AppDbContext dbContext = postgres.CreateDbContext();
        Document document = await dbContext.Documents
            .Include(candidate => candidate.Chunks)
            .SingleAsync(candidate => candidate.TenantId == tenantId);

        Assert.Equal(result.DocumentId, document.DocumentId);
        Assert.Equal(result.ChunkCount, document.Chunks.Count);
        Assert.Equal(
            Enumerable.Range(0, result.ChunkCount),
            document.Chunks.OrderBy(chunk => chunk.ChunkIndex).Select(chunk => chunk.ChunkIndex));
    }

    private static async Task<DocumentIngestionResult[]> IngestConcurrentlyAsync(
        HttpClient client,
        DocumentIngestionRequest request)
    {
        Task<DocumentIngestionResult> first = IngestAsync(client, request);
        Task<DocumentIngestionResult> second = IngestAsync(client, request);
        return await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(30));
    }

    private static async Task<DocumentIngestionResult> IngestAsync(
        HttpClient client,
        DocumentIngestionRequest request)
    {
        using HttpResponseMessage response = await client.PostTenantJsonAsync("/api/v1/documents", request);
        return await ReadAsync<DocumentIngestionResult>(response);
    }

    private static async Task<SemanticSearchResponse> SearchAsync(
        HttpClient client,
        SemanticSearchRequest request)
    {
        using HttpResponseMessage response = await client.PostTenantJsonAsync("/api/v1/search", request);
        return await ReadAsync<SemanticSearchResponse>(response);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        string body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"HTTP {(int)response.StatusCode}: {body}");
        T? value = await response.Content.ReadFromJsonAsync<T>();
        return Assert.IsType<T>(value);
    }

    private static Tenant CreateTenant(string label)
    {
        Guid id = Guid.NewGuid();
        return new Tenant
        {
            TenantId = id,
            ApiKeyHash = TenantApiTestExtensions.HashFor(id),
            TenantName = label,
            TenantDescription = $"Integration test tenant {label}",
            Email = $"{label}-{id:N}@example.test",
            CreatedDate = DateTime.UtcNow,
            LastModifiedDate = DateTime.UtcNow,
            IsActive = true
        };
    }

    private static Chunk CreateChunk(Document document, int index, float[] embedding)
    {
        return new Chunk
        {
            DocumentId = document.DocumentId,
            TenantId = document.TenantId,
            ChunkIndex = index,
            TextContent = $"chunk {index}",
            TokenCount = 2,
            Embedding = new Vector(embedding),
            MetaData = new ChunkMetaData { ChunkStrategy = "integration_test" },
            CreatedAt = DateTime.UtcNow
        };
    }

    private static string ComputeHash(string content)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)))
            .ToLowerInvariant();
    }

    private sealed class PostgresApiFactory(
        string connectionString,
        ControlledEmbeddingService embeddings) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("ConnectionStrings:DefaultConnection", connectionString);
            builder.UseSetting("App:RateLimiting:Enabled", "false");
            builder.UseSetting("OpenTelemetry:ExportEnabled", "false");
            builder.ConfigureLogging(logging => logging.ClearProviders());
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IEmbeddingService>();
                services.AddSingleton<IEmbeddingService>(embeddings);
            });
        }
    }

    private sealed class ControlledEmbeddingService : IEmbeddingService
    {
        private BatchBarrier? _batchBarrier;

        public int BatchDimension { get; init; } = 768;

        public void UseBatchBarrier(int participants)
        {
            _batchBarrier = new BatchBarrier(participants);
        }

        public Task<float[]> EmbedAsync(
            string text,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(CreateVector(text));
        }

        public async Task<IReadOnlyList<float[]>> EmbedBatchAsync(
            IReadOnlyList<string> texts,
            CancellationToken cancellationToken = default)
        {
            BatchBarrier? barrier = _batchBarrier;
            if (barrier is not null)
                await barrier.SignalAndWaitAsync(cancellationToken);

            return texts.Select(text => CreateVector(text, BatchDimension)).ToArray();
        }

        public static float[] CreateVector(string text, int dimension = 768)
        {
            var vector = new float[dimension];
            Array.Fill(vector, 0.00001f);

            if (text.Contains("DIAGONAL", StringComparison.OrdinalIgnoreCase))
            {
                vector[0] = 0.8f;
                vector[1] = 0.6f;
            }
            else if (text.Contains("BETA", StringComparison.OrdinalIgnoreCase))
            {
                vector[1] = 1f;
            }
            else
            {
                vector[0] = 1f;
            }

            return vector;
        }
    }

    private sealed class BatchBarrier(int participants)
    {
        private readonly TaskCompletionSource _allArrived =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrivalCount;

        public async Task SignalAndWaitAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _arrivalCount) == participants)
                _allArrived.TrySetResult();

            await _allArrived.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        }
    }
}
