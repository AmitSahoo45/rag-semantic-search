namespace SemanticRagSearch.Api.Search;

public sealed record SemanticSearchRequest(
    Guid TenantId,
    string Query,
    int? TopK,
    double? MinSimilarity,
    string? RetrievalMode = null) : Authentication.ITenantRequest;
