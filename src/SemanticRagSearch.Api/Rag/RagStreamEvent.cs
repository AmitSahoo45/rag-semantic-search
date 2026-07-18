namespace SemanticRagSearch.Api.Rag;

public sealed record RagStreamEvent(
    string? Text,
    bool IsCompleted,
    IReadOnlyList<RagSource>? Sources,
    RagExecutionMetadata? Metadata);
