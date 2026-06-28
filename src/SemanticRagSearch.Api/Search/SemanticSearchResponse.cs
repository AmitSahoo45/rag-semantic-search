namespace SemanticRagSearch.Api.Search;

public sealed record SemanticSearchResponse(
    Guid TenantId,
    string Query,
    int TopK,
    double MinSimilarity,
    IReadOnlyList<SemanticSearchMatch> Matches);
