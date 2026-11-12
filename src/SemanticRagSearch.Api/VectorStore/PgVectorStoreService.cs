using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Pgvector;
using SemanticRagSearch.Api.Domain;

namespace SemanticRagSearch.Api.VectorStore;

public class PgVectorStoreService : IVectorStoreService
{
    private const string UpsertChunkSql = """
        INSERT INTO chunks (
            chunk_id,
            document_id,
            tenant_id,
            chunk_index,
            text_content,
            token_count,
            embedding,
            metadata,
            created_at
        )
        VALUES (
            @chunk_id,
            @document_id,
            @tenant_id,
            @chunk_index,
            @text_content,
            @token_count,
            @embedding,
            @metadata,
            @created_at
        )
        ON CONFLICT (document_id, chunk_index)
        DO UPDATE SET
            text_content = EXCLUDED.text_content,
            token_count = EXCLUDED.token_count,
            embedding = EXCLUDED.embedding,
            metadata = EXCLUDED.metadata;
        """;

    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<PgVectorStoreService> _logger;

    public PgVectorStoreService(NpgsqlDataSource dataSource, ILogger<PgVectorStoreService> logger)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task StoreAsync(Chunk chunk, CancellationToken cancellationToken = default)
    {
        ValidateChunk(chunk);

        await using NpgsqlConnection connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(UpsertChunkSql, connection);
        AddChunkParameters(command.Parameters, chunk);

        await command.ExecuteNonQueryAsync(cancellationToken);

        _logger.LogInformation(
            "Stored chunk {ChunkId} for document {DocumentId}.",
            chunk.ChunkId,
            chunk.DocumentId);
    }

    public async Task StoreBatchAsync(IReadOnlyList<Chunk> chunks, CancellationToken cancellationToken = default)
    {
        if (chunks is null)
            throw new ArgumentNullException(nameof(chunks));

        if (chunks.Count == 0)
            throw new ArgumentException("At least one chunk is required.", nameof(chunks));

        foreach (Chunk chunk in chunks)
        {
            ValidateChunk(chunk);
        }

        await using NpgsqlConnection connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var batch = new NpgsqlBatch(connection, transaction);

        foreach (Chunk chunk in chunks)
        {
            var batchCommand = new NpgsqlBatchCommand(UpsertChunkSql);
            AddChunkParameters(batchCommand.Parameters, chunk);
            batch.BatchCommands.Add(batchCommand);
        }

        await batch.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        _logger.LogInformation("Stored {ChunkCount} chunk(s).", chunks.Count);
    }

    public async Task<IReadOnlyList<SimilarityResult>> SearchSimilarAsync(
        Guid tenantId,
        Vector queryEmbedding,
        int topK,
        double minSimilarity,
        CancellationToken cancellationToken = default)
    {
        if (tenantId == Guid.Empty)
            throw new ArgumentException("TenantId cannot be empty.", nameof(tenantId));

        ValidateEmbedding(queryEmbedding, nameof(queryEmbedding));

        if (topK <= 0)
            throw new ArgumentOutOfRangeException(nameof(topK), "TopK must be greater than zero.");

        if (minSimilarity < 0 || minSimilarity > 1)
            throw new ArgumentOutOfRangeException(nameof(minSimilarity), "MinSimilarity must be between 0 and 1.");

        const string sql = """
            SELECT
                chunk_id,
                document_id,
                tenant_id,
                chunk_index,
                text_content,
                token_count,
                embedding,
                metadata,
                created_at,
                1 - (embedding <=> @query_embedding) AS score
            FROM chunks
            WHERE tenant_id = @tenant_id
              AND 1 - (embedding <=> @query_embedding) >= @min_similarity
            ORDER BY embedding <=> @query_embedding
            LIMIT @top_k;
            """;

        await using NpgsqlConnection connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);

        // The global HNSW index filters tenants after finding candidates. Continue
        // scanning past other tenants, preserving distance order (pgvector >= 0.8.0).
        // SET LOCAL keeps this tuning inside the search transaction and out of the pool.
        await using (var settings = new NpgsqlCommand(
            "SET LOCAL hnsw.iterative_scan = strict_order;", connection, transaction))
        {
            await settings.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("tenant_id", tenantId);
        command.Parameters.AddWithValue("query_embedding", queryEmbedding);
        command.Parameters.AddWithValue("min_similarity", minSimilarity);
        command.Parameters.AddWithValue("top_k", topK);

        var results = new List<SimilarityResult>();

        await using (NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                Chunk chunk = ReadChunk(reader);
                double score = reader.GetDouble(9);
                results.Add(new SimilarityResult(chunk, score));
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return results;
    }

    public async Task DeleteByDocumentIdAsync(Guid documentId, CancellationToken cancellationToken = default)
    {
        if (documentId == Guid.Empty)
            throw new ArgumentException("DocumentId cannot be empty.", nameof(documentId));

        await using NpgsqlConnection connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("DELETE FROM chunks WHERE document_id = @document_id;", connection);
        command.Parameters.AddWithValue("document_id", documentId);

        int deletedCount = await command.ExecuteNonQueryAsync(cancellationToken);

        _logger.LogInformation(
            "Deleted {ChunkCount} chunk(s) for document {DocumentId}.",
            deletedCount,
            documentId);
    }

    private static void ValidateChunk(Chunk chunk)
    {
        if (chunk is null)
            throw new ArgumentNullException(nameof(chunk));

        if (chunk.ChunkId == Guid.Empty)
            throw new ArgumentException("ChunkId cannot be empty.", nameof(chunk));

        if (chunk.DocumentId == Guid.Empty)
            throw new ArgumentException("DocumentId cannot be empty.", nameof(chunk));

        if (chunk.TenantId == Guid.Empty)
            throw new ArgumentException("TenantId cannot be empty.", nameof(chunk));

        if (chunk.ChunkIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(chunk), "ChunkIndex cannot be negative.");

        if (string.IsNullOrWhiteSpace(chunk.TextContent))
            throw new ArgumentException("Chunk text content cannot be empty.", nameof(chunk));

        ValidateEmbedding(chunk.Embedding, nameof(chunk));
    }

    private static void ValidateEmbedding(Vector embedding, string parameterName)
    {
        if (embedding is null)
            throw new ArgumentNullException(parameterName);

        if (embedding.ToArray().Length == 0)
            throw new ArgumentException("Embedding cannot be empty.", parameterName);
    }

    private static void AddChunkParameters(NpgsqlParameterCollection parameters, Chunk chunk)
    {
        parameters.AddWithValue("chunk_id", chunk.ChunkId);
        parameters.AddWithValue("document_id", chunk.DocumentId);
        parameters.AddWithValue("tenant_id", chunk.TenantId);
        parameters.AddWithValue("chunk_index", chunk.ChunkIndex);
        parameters.AddWithValue("text_content", chunk.TextContent);
        parameters.AddWithValue("token_count", chunk.TokenCount);
        parameters.AddWithValue("embedding", chunk.Embedding);
        parameters.AddWithValue("metadata", NpgsqlDbType.Jsonb, SerializeMetadata(chunk.MetaData));
        parameters.AddWithValue("created_at", chunk.CreatedAt);
    }

    private static string SerializeMetadata(ChunkMetaData? metadata)
    {
        return JsonSerializer.Serialize(metadata ?? new ChunkMetaData());
    }

    private static Chunk ReadChunk(NpgsqlDataReader reader)
    {
        string? metadataJson = reader.IsDBNull(7) ? null : reader.GetString(7);

        return new Chunk
        {
            ChunkId = reader.GetGuid(0),
            DocumentId = reader.GetGuid(1),
            TenantId = reader.GetGuid(2),
            ChunkIndex = reader.GetInt32(3),
            TextContent = reader.GetString(4),
            TokenCount = reader.GetInt32(5),
            Embedding = reader.GetFieldValue<Vector>(6),
            MetaData = DeserializeMetadata(metadataJson),
            CreatedAt = reader.GetDateTime(8)
        };
    }

    private static ChunkMetaData? DeserializeMetadata(string? metadataJson)
    {
        if (string.IsNullOrWhiteSpace(metadataJson))
            return null;

        return JsonSerializer.Deserialize<ChunkMetaData>(metadataJson);
    }
}
