using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Pgvector;
using SemanticRagSearch.Api.Configuration;
using SemanticRagSearch.Api.Data;
using SemanticRagSearch.Api.Domain;
using SemanticRagSearch.Api.Embedding;
using SemanticRagSearch.Api.VectorStore;

namespace SemanticRagSearch.Api.Search;

public class SemanticSearchService : ISemanticSearchService
{
    private const int MaxQueryLength = 2_000;

    private readonly AppDbContext _dbContext;
    private readonly IEmbeddingService _embeddingService;
    private readonly IVectorStoreService _vectorStoreService;
    private readonly SearchSettings _settings;
    private readonly ILogger<SemanticSearchService> _logger;

    public SemanticSearchService(
        AppDbContext dbContext,
        IEmbeddingService embeddingService,
        IVectorStoreService vectorStoreService,
        IOptions<SearchSettings> settings,
        ILogger<SemanticSearchService> logger)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _embeddingService = embeddingService ?? throw new ArgumentNullException(nameof(embeddingService));
        _vectorStoreService = vectorStoreService ?? throw new ArgumentNullException(nameof(vectorStoreService));
        _settings = settings?.Value ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        ValidateSettings(_settings);
    }

    public async Task<SemanticSearchResponse> SearchAsync(
        SemanticSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);

        int topK = ResolveTopK(request.TopK);
        double minSimilarity = ResolveMinSimilarity(request.MinSimilarity);
        string normalizedQuery = request.Query.Trim();

        bool tenantExists = await _dbContext.Tenants
            .AnyAsync(t => t.TenantId == request.TenantId && t.IsActive, cancellationToken);

        if (!tenantExists)
            throw new KeyNotFoundException($"Active tenant '{request.TenantId}' was not found.");

        float[] queryEmbedding = await _embeddingService.EmbedAsync(normalizedQuery, cancellationToken);

        IReadOnlyList<SimilarityResult> similarityResults = await _vectorStoreService.SearchSimilarAsync(
            request.TenantId,
            new Vector(queryEmbedding),
            topK,
            minSimilarity,
            cancellationToken);

        Dictionary<Guid, DocumentSummary> documentsById = await LoadDocumentSummariesAsync(
            request.TenantId,
            similarityResults.Select(r => r.Chunk.DocumentId).Distinct().ToArray(),
            cancellationToken);

        IReadOnlyList<SemanticSearchMatch> matches = similarityResults
            .Select(result => CreateMatch(result, documentsById))
            .ToArray();

        _logger.LogInformation(
            "Semantic search returned {MatchCount} match(es) for tenant {TenantId}.",
            matches.Count,
            request.TenantId);

        return new SemanticSearchResponse(
            request.TenantId,
            normalizedQuery,
            topK,
            minSimilarity,
            matches);
    }

    private async Task<Dictionary<Guid, DocumentSummary>> LoadDocumentSummariesAsync(
        Guid tenantId,
        Guid[] documentIds,
        CancellationToken cancellationToken)
    {
        if (documentIds.Length == 0)
            return [];

        return await _dbContext.Documents
            .AsNoTracking()
            .Where(d => d.TenantId == tenantId && documentIds.Contains(d.DocumentId))
            .Select(d => new DocumentSummary(d.DocumentId, d.Title, d.SourceUrl))
            .ToDictionaryAsync(d => d.DocumentId, cancellationToken);
    }

    private static SemanticSearchMatch CreateMatch(
        SimilarityResult result,
        IReadOnlyDictionary<Guid, DocumentSummary> documentsById)
    {
        Chunk chunk = result.Chunk;

        documentsById.TryGetValue(chunk.DocumentId, out DocumentSummary? document);

        return new SemanticSearchMatch(
            chunk.ChunkId,
            chunk.DocumentId,
            document?.Title ?? string.Empty,
            document?.SourceUrl,
            chunk.ChunkIndex,
            chunk.TextContent,
            result.Score);
    }

    private int ResolveTopK(int? requestedTopK)
    {
        int topK = requestedTopK ?? _settings.DefaultTopK;

        if (topK <= 0)
            throw new ArgumentOutOfRangeException(nameof(requestedTopK), "TopK must be greater than zero.");

        if (topK > _settings.MaxTopK)
            throw new ArgumentOutOfRangeException(nameof(requestedTopK), $"TopK cannot exceed {_settings.MaxTopK}.");

        return topK;
    }

    private double ResolveMinSimilarity(double? requestedMinSimilarity)
    {
        double minSimilarity = requestedMinSimilarity ?? _settings.MinSimilarity;

        if (minSimilarity < 0 || minSimilarity > 1)
            throw new ArgumentOutOfRangeException(nameof(requestedMinSimilarity), "MinSimilarity must be between 0 and 1.");

        return minSimilarity;
    }

    private static void ValidateRequest(SemanticSearchRequest request)
    {
        if (request is null)
            throw new ArgumentNullException(nameof(request));

        if (request.TenantId == Guid.Empty)
            throw new ArgumentException("TenantId cannot be empty.", nameof(request));

        if (string.IsNullOrWhiteSpace(request.Query))
            throw new ArgumentException("Query cannot be empty.", nameof(request));

        if (request.Query.Trim().Length > MaxQueryLength)
            throw new ArgumentException($"Query cannot exceed {MaxQueryLength} characters.", nameof(request));
    }

    private static void ValidateSettings(SearchSettings settings)
    {
        if (settings.DefaultTopK <= 0)
            throw new InvalidOperationException("DefaultTopK must be greater than zero.");

        if (settings.MaxTopK <= 0)
            throw new InvalidOperationException("MaxTopK must be greater than zero.");

        if (settings.DefaultTopK > settings.MaxTopK)
            throw new InvalidOperationException("DefaultTopK cannot be greater than MaxTopK.");

        if (settings.MinSimilarity < 0 || settings.MinSimilarity > 1)
            throw new InvalidOperationException("MinSimilarity must be between 0 and 1.");
    }

    private sealed record DocumentSummary(Guid DocumentId, string Title, string? SourceUrl);
}
