using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SemanticRagSearch.Api.Chunking;
using SemanticRagSearch.Api.Data;
using SemanticRagSearch.Api.Domain;
using SemanticRagSearch.Api.Embedding;
using SemanticRagSearch.Api.Ingestion;

namespace SemanticRagSearch.UnitTests;

public class DocumentIngestionValidationTests
{
    public static IEnumerable<object?[]> InvalidRequests()
    {
        var valid = new DocumentIngestionRequest(Guid.NewGuid(), "Title", null, "Content");
        yield return [null];
        yield return [valid with { TenantId = Guid.Empty }];
        yield return [valid with { Title = " " }];
        yield return [valid with { Title = new string('x', 301) }];
        yield return [valid with { SourceUrl = new string('x', 2049) }];
        yield return [valid with { Content = " " }];
    }

    [Theory]
    [MemberData(nameof(InvalidRequests))]
    public async Task InvalidInput_IsRejectedBeforeChunkingOrEmbedding(DocumentIngestionRequest? request)
    {
        await using var context = CreateContext();
        var chunker = new Mock<IChunkingService>(MockBehavior.Strict);
        var embedder = new Mock<IEmbeddingService>(MockBehavior.Strict);
        var service = new DocumentIngestionService(context, chunker.Object, embedder.Object,
            NullLogger<DocumentIngestionService>.Instance);

        await Assert.ThrowsAnyAsync<ArgumentException>(() => service.IngestAsync(request!));

        chunker.VerifyNoOtherCalls();
        embedder.VerifyNoOtherCalls();
        Assert.Empty(context.ChangeTracker.Entries());
    }

    [Theory]
    [InlineData("content-too-long", "Content cannot exceed 500,000 characters.")]
    [InlineData("title-nul", "Title cannot contain NUL characters.")]
    [InlineData("source-url-nul", "SourceUrl cannot contain NUL characters.")]
    [InlineData("content-nul", "Content cannot contain NUL characters.")]
    public async Task OversizedOrNulInput_IsRejectedWithDescriptiveMessage(
        string invalidField,
        string expectedMessage)
    {
        // Requests are built here rather than via MemberData so the 500k-char content stays out of test display names.
        var valid = new DocumentIngestionRequest(Guid.NewGuid(), "Title", null, "Content");
        DocumentIngestionRequest request = invalidField switch
        {
            "content-too-long" => valid with { Content = new string('x', 500_001) },
            "title-nul" => valid with { Title = "Ti\0tle" },
            "source-url-nul" => valid with { SourceUrl = "https://example.test/\0" },
            "content-nul" => valid with { Content = "Con\0tent" },
            _ => throw new ArgumentOutOfRangeException(nameof(invalidField), invalidField, "Unknown test case.")
        };
        await using var context = CreateContext();
        var chunker = new Mock<IChunkingService>(MockBehavior.Strict);
        var embedder = new Mock<IEmbeddingService>(MockBehavior.Strict);
        var service = new DocumentIngestionService(context, chunker.Object, embedder.Object,
            NullLogger<DocumentIngestionService>.Instance);

        ArgumentException exception = await Assert.ThrowsAsync<ArgumentException>(() => service.IngestAsync(request));

        Assert.StartsWith(expectedMessage, exception.Message);
        chunker.VerifyNoOtherCalls();
        embedder.VerifyNoOtherCalls();
        Assert.Empty(context.ChangeTracker.Entries());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrInactiveTenant_IsRejectedBeforeProviderWork(bool exists)
    {
        await using var context = CreateContext();
        Guid tenantId = Guid.NewGuid();
        if (exists)
        {
            context.Tenants.Add(new Tenant
            {
                TenantId = tenantId, TenantName = "Inactive", TenantDescription = "Test",
                Email = "inactive@example.test", IsActive = false
            });
            await context.SaveChangesAsync();
            context.ChangeTracker.Clear();
        }
        var chunker = new Mock<IChunkingService>(MockBehavior.Strict);
        var embedder = new Mock<IEmbeddingService>(MockBehavior.Strict);
        var service = new DocumentIngestionService(context, chunker.Object, embedder.Object,
            NullLogger<DocumentIngestionService>.Instance);

        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.IngestAsync(
            new DocumentIngestionRequest(tenantId, "Title", null, "Content")));

        chunker.VerifyNoOtherCalls();
        embedder.VerifyNoOtherCalls();
        Assert.Empty(await context.Documents.ToListAsync());
    }

    [Fact]
    public async Task EmbeddingCountMismatch_DoesNotPublishDocumentOrChunks()
    {
        await using var context = CreateContext();
        var tenant = new Tenant { TenantName = "Test", TenantDescription = "Test", Email = "test@example.test" };
        context.Tenants.Add(tenant);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        var chunker = new Mock<IChunkingService>();
        chunker.Setup(service => service.ChunkDocument(It.IsAny<Document>(), "Content"))
            .Returns((Document document, string text) => new[]
            {
                new Chunk { DocumentId = document.DocumentId, TenantId = tenant.TenantId, TextContent = text }
            });
        var embedder = new Mock<IEmbeddingService>();
        embedder.Setup(service => service.EmbedBatchAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<float[]>());
        var service = new DocumentIngestionService(context, chunker.Object, embedder.Object,
            NullLogger<DocumentIngestionService>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.IngestAsync(
            new DocumentIngestionRequest(tenant.TenantId, "Title", null, "Content")));

        Assert.Empty(await context.Documents.ToListAsync());
        Assert.Empty(await context.Chunks.ToListAsync());
        Assert.Empty(context.ChangeTracker.Entries());
    }

    private static ValidationDbContext CreateContext() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase($"ingestion-validation-{Guid.NewGuid()}").Options);

    private sealed class ValidationDbContext(DbContextOptions<AppDbContext> options) : AppDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<Chunk>().Ignore(chunk => chunk.Embedding);
            modelBuilder.Entity<Chunk>().Ignore(chunk => chunk.MetaData);
        }
    }
}
