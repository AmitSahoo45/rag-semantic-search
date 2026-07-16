namespace SemanticRagSearch.Api.Rag;

public sealed record RagResponse(
    Guid TenantId,
    string Question,
    int TopK,
    double MinSimilarity,
    string Answer,
    IReadOnlyList<RagSource> Sources);
