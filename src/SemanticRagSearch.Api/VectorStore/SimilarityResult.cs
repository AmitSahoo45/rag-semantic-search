using SemanticRagSearch.Api.Domain;

namespace SemanticRagSearch.Api.VectorStore;

public sealed record SimilarityResult(Chunk Chunk, double Score);
