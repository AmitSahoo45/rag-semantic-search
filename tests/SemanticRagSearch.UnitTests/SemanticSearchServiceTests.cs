using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Pgvector;
using SemanticRagSearch.Api.Configuration;
using SemanticRagSearch.Api.Data;
using SemanticRagSearch.Api.Domain;
using SemanticRagSearch.Api.Embedding;
using SemanticRagSearch.Api.Search;
using SemanticRagSearch.Api.VectorStore;

namespace SemanticRagSearch.UnitTests;

public sealed class SemanticSearchServiceTests
{
    [Theory]
    [InlineData(null, "vector", 2)]
    [InlineData("hybrid", "hybrid", 50)]
    public async Task SearchAsync_ResolvesModeAndUsesBoundedCandidates(string? mode, string resolved, int limit)
    {
        await using AppDbContext db = CreateContext();
        var tenant = new Tenant { TenantName = "test", TenantDescription = "test", Email = "test@example.test" };
        db.Tenants.Add(tenant);
        var document = new Document { TenantId = tenant.TenantId, Title = "Source", ContentHash = "hash" };
        db.Documents.Add(document);
        await db.SaveChangesAsync();
        var candidate = new SimilarityResult(new Chunk
        {
            TenantId = tenant.TenantId, DocumentId = document.DocumentId,
            TextContent = "text", Embedding = new Vector(new[] { 1f })
        }, 0.8);
        var embedding = new Mock<IEmbeddingService>(MockBehavior.Strict);
        embedding.Setup(service => service.EmbedAsync("query", It.IsAny<CancellationToken>())).ReturnsAsync([1f]);
        var vector = new Mock<IVectorStoreService>(MockBehavior.Strict);
        vector.Setup(service => service.SearchSimilarAsync(tenant.TenantId, It.IsAny<Vector>(), limit, 0.5,
            It.IsAny<CancellationToken>())).ReturnsAsync([candidate]);
        var lexical = new Mock<IFullTextSearchService>(MockBehavior.Strict);
        if (mode == "hybrid")
            lexical.Setup(service => service.SearchAsync(tenant.TenantId, "query", It.IsAny<Vector>(), 50, 0.5,
                It.IsAny<CancellationToken>())).ReturnsAsync([candidate]);
        var service = CreateService(db, embedding.Object, vector.Object, lexical.Object);

        SemanticSearchResponse response = await service.SearchAsync(new(tenant.TenantId, " query ", 2, 0.5, mode));

        Assert.Equal(resolved, response.RetrievalMode);
        SemanticSearchMatch match = Assert.Single(response.Matches);
        Assert.Equal(0.8, match.Score);
        Assert.Equal("Source", match.DocumentTitle);
        Assert.Equal(mode == "hybrid", match.RankingScore.HasValue);
        if (mode is null) lexical.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("invalid", 0.5)]
    [InlineData("vector", double.NaN)]
    [InlineData("hybrid", double.PositiveInfinity)]
    public async Task SearchAsync_InvalidRequestFailsBeforeProviders(string mode, double threshold)
    {
        await using AppDbContext db = CreateContext();
        var service = CreateService(db, Mock.Of<IEmbeddingService>(MockBehavior.Strict),
            Mock.Of<IVectorStoreService>(MockBehavior.Strict), Mock.Of<IFullTextSearchService>(MockBehavior.Strict));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => service.SearchAsync(new(Guid.NewGuid(), "query", 2, threshold, mode)));
    }

    [Fact]
    public async Task SearchAsync_RejectsQueryContainingNulCharacterBeforeProviders()
    {
        await using AppDbContext db = CreateContext();
        var service = CreateService(db, Mock.Of<IEmbeddingService>(MockBehavior.Strict),
            Mock.Of<IVectorStoreService>(MockBehavior.Strict), Mock.Of<IFullTextSearchService>(MockBehavior.Strict));
        ArgumentException exception = await Assert.ThrowsAsync<ArgumentException>(
            () => service.SearchAsync(new(Guid.NewGuid(), "query\0text", 2, 0.5, "hybrid")));
        Assert.StartsWith("Query cannot contain NUL characters.", exception.Message);
    }

    [Theory]
    [InlineData(19, 60, "vector")]
    [InlineData(201, 60, "vector")]
    [InlineData(50, 0, "vector")]
    [InlineData(50, 60, "invalid")]
    public void Constructor_RejectsInvalidRetrievalConfiguration(int limit, int constant, string mode)
    {
        using AppDbContext db = CreateContext();
        Assert.Throws<InvalidOperationException>(() => CreateService(db, Mock.Of<IEmbeddingService>(),
            Mock.Of<IVectorStoreService>(), Mock.Of<IFullTextSearchService>(), new SearchSettings
            { CandidateLimit = limit, RrfConstant = constant, RetrievalMode = mode }));
    }

    private static AppDbContext CreateContext() => new SearchTestContext(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private sealed class SearchTestContext(DbContextOptions<AppDbContext> options) : AppDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<Chunk>().Ignore(chunk => chunk.Embedding);
            modelBuilder.Entity<Chunk>().Ignore(chunk => chunk.MetaData);
        }
    }

    private static SemanticSearchService CreateService(AppDbContext db, IEmbeddingService embedding,
        IVectorStoreService vector, IFullTextSearchService lexical, SearchSettings? settings = null) =>
        new(db, embedding, vector, Options.Create(settings ?? new SearchSettings()),
            NullLogger<SemanticSearchService>.Instance, lexical);
}
