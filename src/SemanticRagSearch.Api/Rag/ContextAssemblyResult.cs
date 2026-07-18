namespace SemanticRagSearch.Api.Rag;

public sealed record ContextAssemblyResult(
    string Context,
    IReadOnlyList<RagSource> Sources,
    int EstimatedTokenCount,
    int TokenBudget,
    bool WasTruncated);
