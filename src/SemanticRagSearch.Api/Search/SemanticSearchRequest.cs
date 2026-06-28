namespace SemanticRagSearch.Api.Search;

public sealed record SemanticSearchRequest(
    Guid TenantId,
    string Query,
    int? TopK,
    double? MinSimilarity);
