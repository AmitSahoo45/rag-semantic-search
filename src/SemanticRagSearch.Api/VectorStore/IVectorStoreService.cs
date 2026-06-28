using Pgvector;
using SemanticRagSearch.Api.Domain;

namespace SemanticRagSearch.Api.VectorStore;

public interface IVectorStoreService
{
    Task StoreAsync(Chunk chunk, CancellationToken cancellationToken = default);

    Task StoreBatchAsync(IReadOnlyList<Chunk> chunks, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SimilarityResult>> SearchSimilarAsync(
        Guid tenantId,
        Vector queryEmbedding,
        int topK,
        double minSimilarity,
        CancellationToken cancellationToken = default);

    Task DeleteByDocumentIdAsync(Guid documentId, CancellationToken cancellationToken = default);
}
