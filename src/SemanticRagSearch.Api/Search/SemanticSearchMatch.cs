namespace SemanticRagSearch.Api.Search;

public sealed record SemanticSearchMatch(
    Guid ChunkId,
    Guid DocumentId,
    string DocumentTitle,
    string? SourceUrl,
    int ChunkIndex,
    string TextContent,
    double Score);
