using System.Text.Json;
using Npgsql;
using Pgvector;
using SemanticRagSearch.Api.Domain;
using SemanticRagSearch.Api.VectorStore;

namespace SemanticRagSearch.Api.Search;

public sealed class PgFullTextSearchService(NpgsqlDataSource dataSource) : IFullTextSearchService
{
    public async Task<IReadOnlyList<SimilarityResult>> SearchAsync(
        Guid tenantId,
        string query,
        Vector queryEmbedding,
        int candidateLimit,
        double minSimilarity,
        CancellationToken cancellationToken = default)
    {
        if (tenantId == Guid.Empty) throw new ArgumentException("TenantId cannot be empty.", nameof(tenantId));
        if (string.IsNullOrWhiteSpace(query)) throw new ArgumentException("Query cannot be empty.", nameof(query));
        ArgumentNullException.ThrowIfNull(queryEmbedding);
        if (queryEmbedding.ToArray().Length == 0) throw new ArgumentException("Embedding cannot be empty.", nameof(queryEmbedding));
        if (candidateLimit is <= 0 or > 200) throw new ArgumentOutOfRangeException(nameof(candidateLimit));
        if (!double.IsFinite(minSimilarity) || minSimilarity < 0 || minSimilarity > 1)
            throw new ArgumentOutOfRangeException(nameof(minSimilarity));

        const string sql = """
            SELECT c.chunk_id, c.document_id, c.tenant_id, c.chunk_index,
                c.text_content, c.token_count, c.embedding, c.metadata, c.created_at,
                1 - (c.embedding <=> @query_embedding) AS score
            FROM chunks c
            CROSS JOIN websearch_to_tsquery('english'::regconfig, @query) q
            WHERE c.tenant_id = @tenant_id
              AND c.search_vector @@ q
              AND 1 - (c.embedding <=> @query_embedding) >= @min_similarity
            ORDER BY ts_rank_cd(c.search_vector, q) DESC, c.chunk_id
            LIMIT @candidate_limit;
            """;

        await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("tenant_id", tenantId);
        command.Parameters.AddWithValue("query", query);
        command.Parameters.AddWithValue("query_embedding", queryEmbedding);
        command.Parameters.AddWithValue("min_similarity", minSimilarity);
        command.Parameters.AddWithValue("candidate_limit", candidateLimit);
        var results = new List<SimilarityResult>();
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            string? metadata = reader.IsDBNull(7) ? null : reader.GetString(7);
            var chunk = new Chunk
            {
                ChunkId = reader.GetGuid(0), DocumentId = reader.GetGuid(1), TenantId = reader.GetGuid(2),
                ChunkIndex = reader.GetInt32(3), TextContent = reader.GetString(4), TokenCount = reader.GetInt32(5),
                Embedding = reader.GetFieldValue<Vector>(6), CreatedAt = reader.GetDateTime(8),
                MetaData = string.IsNullOrWhiteSpace(metadata) ? null : JsonSerializer.Deserialize<ChunkMetaData>(metadata)
            };
            results.Add(new SimilarityResult(chunk, reader.GetDouble(9)));
        }
        return results;
    }
}
