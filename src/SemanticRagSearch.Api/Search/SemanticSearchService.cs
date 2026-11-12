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
    private readonly IFullTextSearchService _fullTextSearchService;
    private readonly SearchSettings _settings;
    private readonly ILogger<SemanticSearchService> _logger;

    public SemanticSearchService(
        AppDbContext dbContext,
        IEmbeddingService embeddingService,
        IVectorStoreService vectorStoreService,
        IOptions<SearchSettings> settings,
        ILogger<SemanticSearchService> logger,
        IFullTextSearchService fullTextSearchService)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _embeddingService = embeddingService ?? throw new ArgumentNullException(nameof(embeddingService));
        _vectorStoreService = vectorStoreService ?? throw new ArgumentNullException(nameof(vectorStoreService));
        _fullTextSearchService = fullTextSearchService ?? throw new ArgumentNullException(nameof(fullTextSearchService));
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
        string retrievalMode = ResolveRetrievalMode(request.RetrievalMode);
        string normalizedQuery = request.Query.Trim();

        bool tenantExists = await _dbContext.Tenants
            .AnyAsync(t => t.TenantId == request.TenantId && t.IsActive, cancellationToken);

        if (!tenantExists)
            throw new KeyNotFoundException($"Active tenant '{request.TenantId}' was not found.");

        float[] queryEmbedding = await _embeddingService.EmbedAsync(normalizedQuery, cancellationToken);

        var queryVector = new Vector(queryEmbedding);
        IReadOnlyList<SimilarityResult> similarityResults = await _vectorStoreService.SearchSimilarAsync(
            request.TenantId,
            queryVector,
            retrievalMode == "hybrid" ? _settings.CandidateLimit : topK,
            minSimilarity,
            cancellationToken);

        IReadOnlyList<RankedSearchResult>? rankedResults = null;
        if (retrievalMode == "hybrid")
        {
            IReadOnlyList<SimilarityResult> lexicalResults = await _fullTextSearchService.SearchAsync(
                request.TenantId, normalizedQuery, queryVector, _settings.CandidateLimit,
                minSimilarity, cancellationToken);
            rankedResults = ReciprocalRankFusion.Fuse(similarityResults, lexicalResults, topK, _settings.RrfConstant);
            similarityResults = rankedResults.Select(result => result.Result).ToArray();
        }

        Dictionary<Guid, DocumentSummary> documentsById = await LoadDocumentSummariesAsync(
            request.TenantId,
            similarityResults.Select(r => r.Chunk.DocumentId).Distinct().ToArray(),
            cancellationToken);

        IReadOnlyList<SemanticSearchMatch> matches = rankedResults is null
            ? similarityResults.Select(result => CreateMatch(result, documentsById)).ToArray()
            : rankedResults.Select(result => CreateMatch(result.Result, documentsById)
                with { RankingScore = result.RankingScore }).ToArray();

        _logger.LogInformation(
            "Semantic search returned {MatchCount} match(es) for tenant {TenantId}.",
            matches.Count,
            request.TenantId);

        return new SemanticSearchResponse(
            request.TenantId,
            normalizedQuery,
            topK,
            minSimilarity,
            matches,
            retrievalMode);
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

        if (!double.IsFinite(minSimilarity) || minSimilarity < 0 || minSimilarity > 1)
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

        if (request.Query.Contains('\0'))
            throw new ArgumentException("Query cannot contain NUL characters.", nameof(request));

        if (request.Query.Trim().Length > MaxQueryLength)
            throw new ArgumentException($"Query cannot exceed {MaxQueryLength} characters.", nameof(request));
    }

    private string ResolveRetrievalMode(string? requestedMode)
    {
        string mode = requestedMode ?? _settings.RetrievalMode;
        if (mode is not ("vector" or "hybrid"))
            throw new ArgumentException("RetrievalMode must be 'vector' or 'hybrid'.", nameof(requestedMode));
        return mode;
    }

    private static void ValidateSettings(SearchSettings settings)
    {
        if (settings.DefaultTopK <= 0)
            throw new InvalidOperationException("DefaultTopK must be greater than zero.");

        if (settings.MaxTopK <= 0)
            throw new InvalidOperationException("MaxTopK must be greater than zero.");

        if (settings.DefaultTopK > settings.MaxTopK)
            throw new InvalidOperationException("DefaultTopK cannot be greater than MaxTopK.");

        if (!double.IsFinite(settings.MinSimilarity) || settings.MinSimilarity < 0 || settings.MinSimilarity > 1)
            throw new InvalidOperationException("MinSimilarity must be between 0 and 1.");

        if (settings.RetrievalMode is not ("vector" or "hybrid"))
            throw new InvalidOperationException("RetrievalMode must be 'vector' or 'hybrid'.");
        if (settings.CandidateLimit < settings.MaxTopK || settings.CandidateLimit > 200)
            throw new InvalidOperationException("CandidateLimit must be at least MaxTopK and at most 200.");
        if (settings.RrfConstant <= 0)
            throw new InvalidOperationException("RrfConstant must be greater than zero.");
    }

    private sealed record DocumentSummary(Guid DocumentId, string Title, string? SourceUrl);
}
