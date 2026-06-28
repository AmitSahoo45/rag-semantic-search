namespace SemanticRagSearch.Api.Ingestion;

public sealed record DocumentIngestionResult(
    Guid DocumentId,
    int ChunkCount,
    bool WasDuplicate);
