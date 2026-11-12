using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Npgsql;
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
    public async Task IngestAsync_ReturnsCommittedWinnerWhenNewDocumentRaceLoses()
    {
        const string content = "retry content";
        const string contentHash = "228f1a3808d21637a5fb75d024db2a764a498292f9da1eff946568c374d09d9b";
        string databaseName = $"document-ingestion-{Guid.NewGuid()}";
        var databaseRoot = new InMemoryDatabaseRoot();
        await using InMemoryAppDbContext losingContext = CreateDbContext(databaseName, databaseRoot);
        Guid tenantId = Guid.NewGuid();
        Guid winnerDocumentId = Guid.NewGuid();

        losingContext.Tenants.Add(new Tenant
        {
            TenantId = tenantId,
            TenantName = "Test tenant",
            TenantDescription = "Tenant for concurrent document race tests",
            Email = "concurrent-document-race@example.test"
        });
        await losingContext.SaveChangesAsync();
        losingContext.ChangeTracker.Clear();
        losingContext.ResetSaveTracking();

        losingContext.FailNextSave(
            CreateDatabaseException("IX_documents_tenant_id_content_hash"),
            async () =>
            {
                await using InMemoryAppDbContext winningContext = CreateDbContext(databaseName, databaseRoot);
                winningContext.Documents.Add(new Document
                {
                    DocumentId = winnerDocumentId,
                    TenantId = tenantId,
                    Title = "Winning document",
                    ContentHash = contentHash
                });
                winningContext.Chunks.Add(new Chunk
                {
                    DocumentId = winnerDocumentId,
                    TenantId = tenantId,
                    ChunkIndex = 0,
                    TextContent = content,
                    TokenCount = 3
                });
                await winningContext.SaveChangesAsync();
            });

        var embeddingService = new Mock<IEmbeddingService>(MockBehavior.Strict);
        embeddingService
            .Setup(service => service.EmbedBatchAsync(
                It.Is<IReadOnlyList<string>>(texts => texts.Count == 1 && texts[0] == content),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([new float[768]]);
        var service = new DocumentIngestionService(
            losingContext,
            CreateChunkingService(),
            embeddingService.Object,
            NullLogger<DocumentIngestionService>.Instance);

        DocumentIngestionResult result = await service.IngestAsync(
            new DocumentIngestionRequest(tenantId, "Losing document", null, content));

        Assert.True(result.WasDuplicate);
        Assert.Equal(winnerDocumentId, result.DocumentId);
        Assert.Equal(1, result.ChunkCount);
        Assert.Equal(1, losingContext.SaveChangesCallCount);
        Assert.Equal(1, losingContext.AddedDocumentCountAtLastSave);
        Assert.Equal(1, losingContext.AddedChunkCountAtLastSave);
        Assert.Empty(losingContext.ChangeTracker.Entries());

        await using InMemoryAppDbContext verificationContext = CreateDbContext(databaseName, databaseRoot);
        Assert.Single(await verificationContext.Documents.ToListAsync());
        Assert.Single(await verificationContext.Chunks.ToListAsync());
    }

    [Fact]
    public async Task IngestAsync_ReturnsCompletedDocumentWhenZeroChunkRetryRaceLoses()
    {
        const string content = "retry content";
        const string contentHash = "228f1a3808d21637a5fb75d024db2a764a498292f9da1eff946568c374d09d9b";
        string databaseName = $"document-ingestion-{Guid.NewGuid()}";
        var databaseRoot = new InMemoryDatabaseRoot();
        await using InMemoryAppDbContext losingContext = CreateDbContext(databaseName, databaseRoot);
        Guid tenantId = Guid.NewGuid();
        Guid documentId = Guid.NewGuid();

        losingContext.Tenants.Add(new Tenant
        {
            TenantId = tenantId,
            TenantName = "Test tenant",
            TenantDescription = "Tenant for concurrent chunk race tests",
            Email = "concurrent-chunk-race@example.test"
        });
        losingContext.Documents.Add(new Document
        {
            DocumentId = documentId,
            TenantId = tenantId,
            Title = "Incomplete document",
            ContentHash = contentHash
        });
        await losingContext.SaveChangesAsync();
        losingContext.ChangeTracker.Clear();
        losingContext.ResetSaveTracking();

        losingContext.FailNextSave(
            CreateDatabaseException("IX_chunks_document_id_chunk_index"),
            async () =>
            {
                await using InMemoryAppDbContext winningContext = CreateDbContext(databaseName, databaseRoot);
                winningContext.Chunks.Add(new Chunk
                {
                    DocumentId = documentId,
                    TenantId = tenantId,
                    ChunkIndex = 0,
                    TextContent = content,
                    TokenCount = 3
                });
                await winningContext.SaveChangesAsync();
            });

        var embeddingService = new Mock<IEmbeddingService>(MockBehavior.Strict);
        embeddingService
            .Setup(service => service.EmbedBatchAsync(
                It.Is<IReadOnlyList<string>>(texts => texts.Count == 1 && texts[0] == content),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([new float[768]]);
        var service = new DocumentIngestionService(
            losingContext,
            CreateChunkingService(),
            embeddingService.Object,
            NullLogger<DocumentIngestionService>.Instance);

        DocumentIngestionResult result = await service.IngestAsync(
            new DocumentIngestionRequest(tenantId, "Incomplete document", null, content));

        Assert.True(result.WasDuplicate);
        Assert.Equal(documentId, result.DocumentId);
        Assert.Equal(1, result.ChunkCount);
        Assert.Equal(1, losingContext.SaveChangesCallCount);
        Assert.Equal(0, losingContext.AddedDocumentCountAtLastSave);
        Assert.Equal(1, losingContext.AddedChunkCountAtLastSave);
        Assert.Empty(losingContext.ChangeTracker.Entries());

        await using InMemoryAppDbContext verificationContext = CreateDbContext(databaseName, databaseRoot);
        Assert.Single(await verificationContext.Documents.ToListAsync());
        Assert.Single(await verificationContext.Chunks.ToListAsync());
    }

    [Fact]
    public async Task IngestAsync_RethrowsOriginalExceptionWhenRaceWinnerIsMissing()
    {
        const string content = "retry content";
        await using InMemoryAppDbContext dbContext = CreateDbContext();
        Guid tenantId = Guid.NewGuid();
        DbUpdateException expectedException = CreateDatabaseException("IX_documents_tenant_id_content_hash");

        dbContext.Tenants.Add(new Tenant
        {
            TenantId = tenantId,
            TenantName = "Test tenant",
            TenantDescription = "Tenant for missing concurrent winner tests",
            Email = "missing-concurrent-winner@example.test"
        });
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();
        dbContext.ResetSaveTracking();
        dbContext.FailNextSave(expectedException);

        var embeddingService = new Mock<IEmbeddingService>(MockBehavior.Strict);
        embeddingService
            .Setup(service => service.EmbedBatchAsync(
                It.Is<IReadOnlyList<string>>(texts => texts.Count == 1 && texts[0] == content),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([new float[768]]);
        var service = new DocumentIngestionService(
            dbContext,
            CreateChunkingService(),
            embeddingService.Object,
            NullLogger<DocumentIngestionService>.Instance);

        DbUpdateException actualException = await Assert.ThrowsAsync<DbUpdateException>(() => service.IngestAsync(
            new DocumentIngestionRequest(tenantId, "Missing winner", null, content)));

        Assert.Same(expectedException, actualException);
    }

    [Fact]
    public async Task IngestAsync_RethrowsOriginalExceptionWhenRaceWinnerHasNoChunks()
    {
        const string content = "retry content";
        const string contentHash = "228f1a3808d21637a5fb75d024db2a764a498292f9da1eff946568c374d09d9b";
        string databaseName = $"document-ingestion-{Guid.NewGuid()}";
        var databaseRoot = new InMemoryDatabaseRoot();
        await using InMemoryAppDbContext losingContext = CreateDbContext(databaseName, databaseRoot);
        Guid tenantId = Guid.NewGuid();
        Guid winnerDocumentId = Guid.NewGuid();
        DbUpdateException expectedException = CreateDatabaseException("IX_documents_tenant_id_content_hash");

        losingContext.Tenants.Add(new Tenant
        {
            TenantId = tenantId,
            TenantName = "Test tenant",
            TenantDescription = "Tenant for incomplete concurrent winner tests",
            Email = "incomplete-concurrent-winner@example.test"
        });
        await losingContext.SaveChangesAsync();
        losingContext.ChangeTracker.Clear();
        losingContext.ResetSaveTracking();
        losingContext.FailNextSave(
            expectedException,
            async () =>
            {
                await using InMemoryAppDbContext winningContext = CreateDbContext(databaseName, databaseRoot);
                winningContext.Documents.Add(new Document
                {
                    DocumentId = winnerDocumentId,
                    TenantId = tenantId,
                    Title = "Incomplete winner",
                    ContentHash = contentHash
                });
                await winningContext.SaveChangesAsync();
            });

        var embeddingService = new Mock<IEmbeddingService>(MockBehavior.Strict);
        embeddingService
            .Setup(service => service.EmbedBatchAsync(
                It.Is<IReadOnlyList<string>>(texts => texts.Count == 1 && texts[0] == content),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([new float[768]]);
        var service = new DocumentIngestionService(
            losingContext,
            CreateChunkingService(),
            embeddingService.Object,
            NullLogger<DocumentIngestionService>.Instance);

        DbUpdateException actualException = await Assert.ThrowsAsync<DbUpdateException>(() => service.IngestAsync(
            new DocumentIngestionRequest(tenantId, "Incomplete winner", null, content)));

        Assert.Same(expectedException, actualException);
    }

    [Fact]
    public async Task IngestAsync_PreservesCancellationDuringRaceRecovery()
    {
        const string content = "retry content";
        const string contentHash = "228f1a3808d21637a5fb75d024db2a764a498292f9da1eff946568c374d09d9b";
        string databaseName = $"document-ingestion-{Guid.NewGuid()}";
        var databaseRoot = new InMemoryDatabaseRoot();
        await using InMemoryAppDbContext losingContext = CreateDbContext(databaseName, databaseRoot);
        Guid tenantId = Guid.NewGuid();
        Guid winnerDocumentId = Guid.NewGuid();
        using var cancellation = new CancellationTokenSource();

        losingContext.Tenants.Add(new Tenant
        {
            TenantId = tenantId,
            TenantName = "Test tenant",
            TenantDescription = "Tenant for concurrent recovery cancellation tests",
            Email = "concurrent-recovery-cancellation@example.test"
        });
        await losingContext.SaveChangesAsync(CancellationToken.None);
        losingContext.ChangeTracker.Clear();
        losingContext.ResetSaveTracking();
        losingContext.FailNextSave(
            CreateDatabaseException("IX_documents_tenant_id_content_hash"),
            async () =>
            {
                await using InMemoryAppDbContext winningContext = CreateDbContext(databaseName, databaseRoot);
                winningContext.Documents.Add(new Document
                {
                    DocumentId = winnerDocumentId,
                    TenantId = tenantId,
                    Title = "Completed winner",
                    ContentHash = contentHash
                });
                winningContext.Chunks.Add(new Chunk
                {
                    DocumentId = winnerDocumentId,
                    TenantId = tenantId,
                    ChunkIndex = 0,
                    TextContent = content,
                    TokenCount = 3
                });
                await winningContext.SaveChangesAsync(CancellationToken.None);
                cancellation.Cancel();
            });

        var embeddingService = new Mock<IEmbeddingService>(MockBehavior.Strict);
        embeddingService
            .Setup(service => service.EmbedBatchAsync(
                It.Is<IReadOnlyList<string>>(texts => texts.Count == 1 && texts[0] == content),
                It.Is<CancellationToken>(token => token == cancellation.Token)))
            .ReturnsAsync([new float[768]]);
        var service = new DocumentIngestionService(
            losingContext,
            CreateChunkingService(),
            embeddingService.Object,
            NullLogger<DocumentIngestionService>.Instance);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.IngestAsync(
            new DocumentIngestionRequest(tenantId, "Canceled recovery", null, content),
            cancellation.Token));
    }

    [Theory]
    [InlineData("IX_other_constraint", PostgresErrorCodes.UniqueViolation)]
    [InlineData("IX_documents_tenant_id_content_hash", "23503")]
    [InlineData("IX_chunks_document_id_chunk_index", PostgresErrorCodes.UniqueViolation)]
    public async Task IngestAsync_RethrowsNewDocumentDatabaseFailureWhenViolationIsNotRecoverable(
        string constraintName,
        string sqlState)
    {
        const string content = "retry content";
        const string contentHash = "228f1a3808d21637a5fb75d024db2a764a498292f9da1eff946568c374d09d9b";
        string databaseName = $"document-ingestion-{Guid.NewGuid()}";
        var databaseRoot = new InMemoryDatabaseRoot();
        await using InMemoryAppDbContext dbContext = CreateDbContext(databaseName, databaseRoot);
        Guid tenantId = Guid.NewGuid();
        Guid winnerDocumentId = Guid.NewGuid();
        DbUpdateException expectedException = CreateDatabaseException(constraintName, sqlState);

        dbContext.Tenants.Add(new Tenant
        {
            TenantId = tenantId,
            TenantName = "Test tenant",
            TenantDescription = "Tenant for unrecoverable database failure tests",
            Email = "unrecoverable-database-failure@example.test"
        });
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();
        dbContext.ResetSaveTracking();
        dbContext.FailNextSave(
            expectedException,
            async () =>
            {
                await using InMemoryAppDbContext winningContext = CreateDbContext(databaseName, databaseRoot);
                winningContext.Documents.Add(new Document
                {
                    DocumentId = winnerDocumentId,
                    TenantId = tenantId,
                    Title = "Completed winner",
                    ContentHash = contentHash
                });
                winningContext.Chunks.Add(new Chunk
                {
                    DocumentId = winnerDocumentId,
                    TenantId = tenantId,
                    ChunkIndex = 0,
                    TextContent = content,
                    TokenCount = 3
                });
                await winningContext.SaveChangesAsync();
            });

        var embeddingService = new Mock<IEmbeddingService>(MockBehavior.Strict);
        embeddingService
            .Setup(service => service.EmbedBatchAsync(
                It.Is<IReadOnlyList<string>>(texts => texts.Count == 1 && texts[0] == content),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([new float[768]]);
        var service = new DocumentIngestionService(
            dbContext,
            CreateChunkingService(),
            embeddingService.Object,
            NullLogger<DocumentIngestionService>.Instance);

        DbUpdateException actualException = await Assert.ThrowsAsync<DbUpdateException>(() => service.IngestAsync(
            new DocumentIngestionRequest(tenantId, "Unrecoverable failure", null, content)));

        Assert.Same(expectedException, actualException);
    }

    [Fact]
    public async Task IngestAsync_RethrowsDatabaseFailureWithNonPostgresInnerException()
    {
        const string content = "retry content";
        const string contentHash = "228f1a3808d21637a5fb75d024db2a764a498292f9da1eff946568c374d09d9b";
        string databaseName = $"document-ingestion-{Guid.NewGuid()}";
        var databaseRoot = new InMemoryDatabaseRoot();
        await using InMemoryAppDbContext dbContext = CreateDbContext(databaseName, databaseRoot);
        Guid tenantId = Guid.NewGuid();
        Guid winnerDocumentId = Guid.NewGuid();
        var expectedException = new DbUpdateException(
            "Test update failure.",
            new InvalidOperationException("Not PostgreSQL."));

        dbContext.Tenants.Add(new Tenant
        {
            TenantId = tenantId,
            TenantName = "Test tenant",
            TenantDescription = "Tenant for non-PostgreSQL failure tests",
            Email = "non-postgresql-failure@example.test"
        });
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();
        dbContext.ResetSaveTracking();
        dbContext.FailNextSave(
            expectedException,
            async () =>
            {
                await using InMemoryAppDbContext winningContext = CreateDbContext(databaseName, databaseRoot);
                winningContext.Documents.Add(new Document
                {
                    DocumentId = winnerDocumentId,
                    TenantId = tenantId,
                    Title = "Completed winner",
                    ContentHash = contentHash
                });
                winningContext.Chunks.Add(new Chunk
                {
                    DocumentId = winnerDocumentId,
                    TenantId = tenantId,
                    ChunkIndex = 0,
                    TextContent = content,
                    TokenCount = 3
                });
                await winningContext.SaveChangesAsync();
            });

        var embeddingService = new Mock<IEmbeddingService>(MockBehavior.Strict);
        embeddingService
            .Setup(service => service.EmbedBatchAsync(
                It.Is<IReadOnlyList<string>>(texts => texts.Count == 1 && texts[0] == content),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([new float[768]]);
        var service = new DocumentIngestionService(
            dbContext,
            CreateChunkingService(),
            embeddingService.Object,
            NullLogger<DocumentIngestionService>.Instance);

        DbUpdateException actualException = await Assert.ThrowsAsync<DbUpdateException>(() => service.IngestAsync(
            new DocumentIngestionRequest(tenantId, "Non-PostgreSQL failure", null, content)));

        Assert.Same(expectedException, actualException);
    }

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
    public async Task IngestAsync_EmbedsChunksInBatchesOfAtMostThirtyTwoInChunkOrder()
    {
        const int chunkCount = 40;
        const int embeddingBatchSize = 32;
        await using InMemoryAppDbContext dbContext = CreateDbContext();
        Guid tenantId = Guid.NewGuid();
        using var cancellation = new CancellationTokenSource();
        var recordedBatches = new List<IReadOnlyList<string>>();
        List<Chunk> producedChunks = [];

        dbContext.Tenants.Add(new Tenant
        {
            TenantId = tenantId,
            TenantName = "Test tenant",
            TenantDescription = "Tenant for embedding batch tests",
            Email = "embedding-batches@example.test"
        });
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();
        dbContext.ResetSaveTracking();

        var chunkingService = new Mock<IChunkingService>(MockBehavior.Strict);
        chunkingService
            .Setup(service => service.ChunkDocument(
                It.IsAny<Document>(),
                It.IsAny<string>()))
            .Returns((Document document, string _) =>
            {
                producedChunks.AddRange(Enumerable.Range(0, chunkCount)
                    .Select(index => new Chunk
                    {
                        DocumentId = document.DocumentId,
                        TenantId = document.TenantId,
                        ChunkIndex = index,
                        TextContent = $"chunk-{index}",
                        TokenCount = 1
                    }));

                return producedChunks;
            });
        var embeddingService = new Mock<IEmbeddingService>(MockBehavior.Strict);
        embeddingService
            .Setup(service => service.EmbedBatchAsync(
                It.IsAny<IReadOnlyList<string>>(),
                It.Is<CancellationToken>(token => token == cancellation.Token)))
            .Returns<IReadOnlyList<string>, CancellationToken>((texts, _) =>
            {
                recordedBatches.Add(texts.ToList());

                // Encode the chunk index in the embedding so chunk-order assignment can be verified.
                IReadOnlyList<float[]> embeddings = texts
                    .Select(text => new[] { (float)int.Parse(text["chunk-".Length..], CultureInfo.InvariantCulture) })
                    .ToList();

                return Task.FromResult(embeddings);
            });
        var service = new DocumentIngestionService(
            dbContext,
            chunkingService.Object,
            embeddingService.Object,
            NullLogger<DocumentIngestionService>.Instance);

        DocumentIngestionResult result = await service.IngestAsync(
            new DocumentIngestionRequest(
                tenantId,
                "Batched document",
                null,
                "content that splits into many chunks"),
            cancellation.Token);

        Assert.False(result.WasDuplicate);
        Assert.Equal(chunkCount, result.ChunkCount);
        Assert.Equal(2, recordedBatches.Count);
        Assert.Equal(embeddingBatchSize, recordedBatches[0].Count);
        Assert.Equal(chunkCount - embeddingBatchSize, recordedBatches[1].Count);
        Assert.All(recordedBatches, batch => Assert.True(batch.Count <= embeddingBatchSize));
        Assert.Equal(
            Enumerable.Range(0, chunkCount).Select(index => $"chunk-{index}"),
            recordedBatches.SelectMany(batch => batch));
        Assert.Equal(chunkCount, producedChunks.Count);
        for (int index = 0; index < chunkCount; index++)
        {
            Assert.Equal(index, producedChunks[index].ChunkIndex);
            Assert.Equal((float)index, Assert.Single(producedChunks[index].Embedding.ToArray()));
        }
        Assert.Equal(1, dbContext.SaveChangesCallCount);
        Assert.Equal(1, dbContext.AddedDocumentCountAtLastSave);
        Assert.Equal(chunkCount, dbContext.AddedChunkCountAtLastSave);
        Assert.Equal(chunkCount, await dbContext.Chunks.CountAsync());
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

    private static InMemoryAppDbContext CreateDbContext(
        string? databaseName = null,
        InMemoryDatabaseRoot? databaseRoot = null)
    {
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName ?? $"document-ingestion-{Guid.NewGuid()}", databaseRoot)
            .Options;

        return new InMemoryAppDbContext(options);
    }

    private static DbUpdateException CreateDatabaseException(
        string constraintName,
        string sqlState = PostgresErrorCodes.UniqueViolation)
    {
        var postgresException = new PostgresException(
            "Test database failure.",
            "ERROR",
            "ERROR",
            sqlState,
            constraintName: constraintName);

        return new DbUpdateException("Test update failure.", postgresException);
    }

    private sealed class InMemoryAppDbContext(DbContextOptions<AppDbContext> options)
        : AppDbContext(options)
    {
        public int SaveChangesCallCount { get; private set; }
        public int AddedDocumentCountAtLastSave { get; private set; }
        public int AddedChunkCountAtLastSave { get; private set; }
        public CancellationToken LastSaveCancellationToken { get; private set; }
        private DbUpdateException? _nextSaveException;
        private Func<Task>? _beforeFailure;

        public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            SaveChangesCallCount++;
            AddedDocumentCountAtLastSave = ChangeTracker.Entries<Document>()
                .Count(entry => entry.State == EntityState.Added);
            AddedChunkCountAtLastSave = ChangeTracker.Entries<Chunk>()
                .Count(entry => entry.State == EntityState.Added);
            LastSaveCancellationToken = cancellationToken;

            DbUpdateException? nextSaveException = _nextSaveException;
            Func<Task>? beforeFailure = _beforeFailure;
            _nextSaveException = null;
            _beforeFailure = null;

            if (nextSaveException is not null)
            {
                if (beforeFailure is not null)
                    await beforeFailure();

                throw nextSaveException;
            }

            return await base.SaveChangesAsync(cancellationToken);
        }

        public void FailNextSave(DbUpdateException exception, Func<Task>? beforeFailure = null)
        {
            _nextSaveException = exception;
            _beforeFailure = beforeFailure;
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
