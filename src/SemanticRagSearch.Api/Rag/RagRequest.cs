namespace SemanticRagSearch.Api.Rag;

public sealed record RagRequest(
    Guid TenantId,
    string Question,
    int? TopK,
    double? MinSimilarity);
