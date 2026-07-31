using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using SemanticRagSearch.Api.Chunking;
using SemanticRagSearch.Api.Configuration;
using SemanticRagSearch.Api.Data;
using SemanticRagSearch.Api.Domain;
using SemanticRagSearch.Api.Embedding;
using SemanticRagSearch.Api.Ingestion;

namespace SemanticRagSearch.UnitTests;

public class DocumentIngestionServiceTests
{
    [Fact]
    public async Task IngestAsync_PersistsNewDocumentAndChunksInSingleSave()
    {
        await using InMemoryAppDbContext dbContext = CreateDbContext();
        Guid tenantId = Guid.NewGuid();
        using var cancellation = new CancellationTokenSource();

        dbContext.Tenants.Add(new Tenant
        {
            TenantId = tenantId,
            TenantName = "Test tenant",
            TenantDescription = "Tenant for atomic ingestion tests",
            Email = "atomic-ingestion@example.test"
        });
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();
        dbContext.ResetSaveTracking();

        var embeddingService = new Mock<IEmbeddingService>(MockBehavior.Strict);
        embeddingService
            .Setup(service => service.EmbedBatchAsync(
                It.Is<IReadOnlyList<string>>(texts =>
                    texts.Count == 1 && texts[0] == "new document content"),
                It.Is<CancellationToken>(token => token == cancellation.Token)))
            .Callback(() => Assert.Null(dbContext.Database.CurrentTransaction))
            .ReturnsAsync([new float[768]]);
        var service = new DocumentIngestionService(
            dbContext,
            CreateChunkingService(),
            embeddingService.Object,
            NullLogger<DocumentIngestionService>.Instance);

        DocumentIngestionResult result = await service.IngestAsync(
            new DocumentIngestionRequest(
                tenantId,
                "New document",
                null,
                "new document content"),
            cancellation.Token);

        Assert.False(result.WasDuplicate);
        Assert.Equal(1, result.ChunkCount);
        Assert.Equal(1, dbContext.SaveChangesCallCount);
        Assert.Equal(1, dbContext.AddedDocumentCountAtLastSave);
        Assert.Equal(1, dbContext.AddedChunkCountAtLastSave);
        Assert.Equal(cancellation.Token, dbContext.LastSaveCancellationToken);
        Assert.Single(await dbContext.Documents.ToListAsync());
        Assert.Single(await dbContext.Chunks.ToListAsync());
    }

    [Fact]
    public async Task IngestAsync_DoesNotPersistDocumentWhenChunkingProducesNoChunks()
    {
        await using InMemoryAppDbContext dbContext = CreateDbContext();
        Guid tenantId = Guid.NewGuid();

        dbContext.Tenants.Add(new Tenant
        {
            TenantId = tenantId,
            TenantName = "Test tenant",
            TenantDescription = "Tenant for empty chunk tests",
            Email = "empty-chunks@example.test"
        });
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();
        dbContext.ResetSaveTracking();

        var chunkingService = new Mock<IChunkingService>(MockBehavior.Strict);
        chunkingService
            .Setup(service => service.ChunkDocument(
                It.IsAny<Document>(),
                It.IsAny<string>()))
            .Returns([]);
        var embeddingService = new Mock<IEmbeddingService>(MockBehavior.Strict);
        var service = new DocumentIngestionService(
            dbContext,
            chunkingService.Object,
            embeddingService.Object,
            NullLogger<DocumentIngestionService>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.IngestAsync(
            new DocumentIngestionRequest(
                tenantId,
                "Empty chunks",
                null,
                "valid non-empty content")));

        Assert.Empty(await dbContext.Documents.ToListAsync());
        Assert.Equal(0, dbContext.SaveChangesCallCount);
    }

    [Fact]
    public async Task IngestAsync_DoesNotPersistDocumentWhenEmbeddingFails()
    {
        await using InMemoryAppDbContext dbContext = CreateDbContext();
        Guid tenantId = Guid.NewGuid();

        dbContext.Tenants.Add(new Tenant
        {
            TenantId = tenantId,
            TenantName = "Test tenant",
            TenantDescription = "Tenant for ingestion failure tests",
            Email = "ingestion-failure@example.test"
        });
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();
        dbContext.ResetSaveTracking();

        var embeddingService = new Mock<IEmbeddingService>(MockBehavior.Strict);
        embeddingService
            .Setup(service => service.EmbedBatchAsync(
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Embedding provider unavailable."));

        var service = new DocumentIngestionService(
            dbContext,
            CreateChunkingService(),
            embeddingService.Object,
            NullLogger<DocumentIngestionService>.Instance);

        await Assert.ThrowsAsync<HttpRequestException>(() => service.IngestAsync(
            new DocumentIngestionRequest(
                tenantId,
                "Failed upload",
                null,
                "content that cannot be embedded")));

        Assert.Empty(await dbContext.Documents.ToListAsync());
        Assert.Empty(await dbContext.Chunks.ToListAsync());
        Assert.Equal(0, dbContext.SaveChangesCallCount);
    }

    [Fact]
    public async Task IngestAsync_DoesNotPersistDocumentWhenEmbeddingIsCanceled()
    {
        await using InMemoryAppDbContext dbContext = CreateDbContext();
        Guid tenantId = Guid.NewGuid();
        using var cancellation = new CancellationTokenSource();

        dbContext.Tenants.Add(new Tenant
        {
            TenantId = tenantId,
            TenantName = "Test tenant",
            TenantDescription = "Tenant for ingestion cancellation tests",
            Email = "ingestion-cancellation@example.test"
        });
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();
        dbContext.ResetSaveTracking();

        var embeddingService = new Mock<IEmbeddingService>(MockBehavior.Strict);
        embeddingService
            .Setup(service => service.EmbedBatchAsync(
                It.IsAny<IReadOnlyList<string>>(),
                It.Is<CancellationToken>(token => token == cancellation.Token)))
            .Returns<IReadOnlyList<string>, CancellationToken>((_, token) =>
            {
                cancellation.Cancel();
                return Task.FromCanceled<IReadOnlyList<float[]>>(token);
            });
        var service = new DocumentIngestionService(
            dbContext,
            CreateChunkingService(),
            embeddingService.Object,
            NullLogger<DocumentIngestionService>.Instance);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.IngestAsync(
            new DocumentIngestionRequest(
                tenantId,
                "Canceled upload",
                null,
                "content canceled during embedding"),
            cancellation.Token));

        Assert.Empty(await dbContext.Documents.ToListAsync());
        Assert.Empty(await dbContext.Chunks.ToListAsync());
        Assert.Equal(0, dbContext.SaveChangesCallCount);
    }

    [Fact]
    public async Task IngestAsync_ReturnsCompletedDocumentAsDuplicate()
    {
        await using InMemoryAppDbContext dbContext = CreateDbContext();
        Guid tenantId = Guid.NewGuid();
        Guid documentId = Guid.NewGuid();

        dbContext.Tenants.Add(new Tenant
        {
            TenantId = tenantId,
            TenantName = "Test tenant",
            TenantDescription = "Tenant for duplicate tests",
            Email = "completed-duplicate@example.test"
        });
        dbContext.Documents.Add(new Document
        {
            DocumentId = documentId,
            TenantId = tenantId,
            Title = "Completed upload",
            ContentHash = "228f1a3808d21637a5fb75d024db2a764a498292f9da1eff946568c374d09d9b"
        });
        dbContext.Chunks.Add(new Chunk
        {
            DocumentId = documentId,
            TenantId = tenantId,
            ChunkIndex = 0,
            TextContent = "retry content",
            TokenCount = 3
        });
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();
        dbContext.ResetSaveTracking();

        var chunkingService = new Mock<IChunkingService>(MockBehavior.Strict);
        var embeddingService = new Mock<IEmbeddingService>(MockBehavior.Strict);
        var service = new DocumentIngestionService(
            dbContext,
            chunkingService.Object,
            embeddingService.Object,
            NullLogger<DocumentIngestionService>.Instance);

        DocumentIngestionResult result = await service.IngestAsync(
            new DocumentIngestionRequest(
                tenantId,
                "Completed upload",
                null,
                "retry content"));

        Assert.True(result.WasDuplicate);
        Assert.Equal(documentId, result.DocumentId);
        Assert.Equal(1, result.ChunkCount);
        Assert.Equal(0, dbContext.SaveChangesCallCount);
    }

    [Fact]
    public async Task IngestAsync_RetriesDocumentThatHasNoChunks()
    {
        await using InMemoryAppDbContext dbContext = CreateDbContext();
        Guid tenantId = Guid.NewGuid();
        Guid documentId = Guid.NewGuid();
        using var cancellation = new CancellationTokenSource();

        dbContext.Tenants.Add(new Tenant
        {
            TenantId = tenantId,
            TenantName = "Test tenant",
            TenantDescription = "Tenant for ingestion recovery tests",
            Email = "ingestion-recovery@example.test"
        });
        dbContext.Documents.Add(new Document
        {
            DocumentId = documentId,
            TenantId = tenantId,
            Title = "Interrupted upload",
            ContentHash = "228f1a3808d21637a5fb75d024db2a764a498292f9da1eff946568c374d09d9b"
        });
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();
        dbContext.ResetSaveTracking();

        var embeddingService = new Mock<IEmbeddingService>(MockBehavior.Strict);
        embeddingService
            .Setup(service => service.EmbedBatchAsync(
                It.Is<IReadOnlyList<string>>(texts =>
                    texts.Count == 1 && texts[0] == "retry content"),
                It.Is<CancellationToken>(token => token == cancellation.Token)))
            .ReturnsAsync([new float[768]]);

        var service = new DocumentIngestionService(
            dbContext,
            CreateChunkingService(),
            embeddingService.Object,
            NullLogger<DocumentIngestionService>.Instance);

        DocumentIngestionResult result = await service.IngestAsync(
            new DocumentIngestionRequest(
                tenantId,
                "Interrupted upload",
                null,
                "retry content"),
            cancellation.Token);

        Assert.False(result.WasDuplicate);
        Assert.Equal(documentId, result.DocumentId);
        Assert.Equal(1, result.ChunkCount);
        Assert.Equal(1, await dbContext.Chunks.CountAsync(c => c.DocumentId == documentId));
        Document persistedDocument = Assert.Single(await dbContext.Documents.ToListAsync());
        Assert.Equal(documentId, persistedDocument.DocumentId);
        Assert.Equal(1, dbContext.SaveChangesCallCount);
        Assert.Equal(0, dbContext.AddedDocumentCountAtLastSave);
        Assert.Equal(1, dbContext.AddedChunkCountAtLastSave);
        Assert.Equal(cancellation.Token, dbContext.LastSaveCancellationToken);
    }

    private static ChunkingService CreateChunkingService()
    {
        return new ChunkingService(
            Options.Create(new ChunkingSettings
            {
                ChunkSize = 100,
                ChunkOverlap = 0
            }),
            NullLogger<ChunkingService>.Instance);
    }

    private static InMemoryAppDbContext CreateDbContext()
    {
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"document-ingestion-{Guid.NewGuid()}")
            .Options;

        return new InMemoryAppDbContext(options);
    }

    private sealed class InMemoryAppDbContext(DbContextOptions<AppDbContext> options)
        : AppDbContext(options)
    {
        public int SaveChangesCallCount { get; private set; }
        public int AddedDocumentCountAtLastSave { get; private set; }
        public int AddedChunkCountAtLastSave { get; private set; }
        public CancellationToken LastSaveCancellationToken { get; private set; }

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            SaveChangesCallCount++;
            AddedDocumentCountAtLastSave = ChangeTracker.Entries<Document>()
                .Count(entry => entry.State == EntityState.Added);
            AddedChunkCountAtLastSave = ChangeTracker.Entries<Chunk>()
                .Count(entry => entry.State == EntityState.Added);
            LastSaveCancellationToken = cancellationToken;

            return base.SaveChangesAsync(cancellationToken);
        }

        public void ResetSaveTracking()
        {
            SaveChangesCallCount = 0;
            AddedDocumentCountAtLastSave = 0;
            AddedChunkCountAtLastSave = 0;
            LastSaveCancellationToken = default;
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<Chunk>().Ignore(chunk => chunk.Embedding);
            modelBuilder.Entity<Chunk>().Ignore(chunk => chunk.MetaData);
        }
    }
}
