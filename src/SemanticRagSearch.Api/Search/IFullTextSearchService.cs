using Pgvector;
using SemanticRagSearch.Api.VectorStore;

namespace SemanticRagSearch.Api.Search;

public interface IFullTextSearchService
{
    Task<IReadOnlyList<SimilarityResult>> SearchAsync(
        Guid tenantId,
        string query,
        Vector queryEmbedding,
        int candidateLimit,
        double minSimilarity,
        CancellationToken cancellationToken = default);
}
