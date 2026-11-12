namespace SemanticRagSearch.Api.Rag;

public sealed record RagExecutionMetadata(
    long SearchLatencyMs,
    long GenerationLatencyMs,
    long? TimeToFirstTextMs,
    string RetrievalMode = "vector");
