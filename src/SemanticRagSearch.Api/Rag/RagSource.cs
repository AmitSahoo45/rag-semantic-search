namespace SemanticRagSearch.Api.Rag;

public sealed record RagSource(
    int SourceNumber,
    Guid ChunkId,
    Guid DocumentId,
    string DocumentTitle,
    string? SourceUrl,
    int ChunkIndex,
    double Score,
    string TextContent);
