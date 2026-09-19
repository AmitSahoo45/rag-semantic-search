using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Pgvector;
using SemanticRagSearch.Api.Chunking;
using SemanticRagSearch.Api.Data;
using SemanticRagSearch.Api.Domain;
using SemanticRagSearch.Api.Embedding;

namespace SemanticRagSearch.Api.Ingestion;

public class DocumentIngestionService : IDocumentIngestionService
{
    private const int MaxTitleLength = 300;
    private const int MaxSourceUrlLength = 2048;
    private const string DocumentContentHashConstraint = "IX_documents_tenant_id_content_hash";
    private const string ChunkIndexConstraint = "IX_chunks_document_id_chunk_index";

    private readonly AppDbContext _dbContext;
    private readonly IChunkingService _chunkingService;
    private readonly IEmbeddingService _embeddingService;
    private readonly ILogger<DocumentIngestionService> _logger;

    public DocumentIngestionService(
        AppDbContext dbContext,
        IChunkingService chunkingService,
        IEmbeddingService embeddingService,
        ILogger<DocumentIngestionService> logger)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _chunkingService = chunkingService ?? throw new ArgumentNullException(nameof(chunkingService));
        _embeddingService = embeddingService ?? throw new ArgumentNullException(nameof(embeddingService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<DocumentIngestionResult> IngestAsync(
        DocumentIngestionRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);

        bool tenantExists = await _dbContext.Tenants
            .AnyAsync(t => t.TenantId == request.TenantId && t.IsActive, cancellationToken);

        if (!tenantExists)
            throw new KeyNotFoundException($"Active tenant '{request.TenantId}' was not found.");

        string contentHash = ComputeSha256Hash(request.Content);

        Document? existingDocument = await _dbContext.Documents
            .AsNoTracking()
            .FirstOrDefaultAsync(
                d => d.TenantId == request.TenantId && d.ContentHash == contentHash,
                cancellationToken);

        Document document;
        bool isNewDocument;

        if (existingDocument is not null)
        {
            int existingChunkCount = await _dbContext.Chunks
                .CountAsync(c => c.DocumentId == existingDocument.DocumentId, cancellationToken);

            if (existingChunkCount > 0)
            {
                _logger.LogInformation(
                    "Skipped duplicate document ingestion for tenant {TenantId}. Existing document: {DocumentId}.",
                    request.TenantId,
                    existingDocument.DocumentId);

                return new DocumentIngestionResult(existingDocument.DocumentId, existingChunkCount, true);
            }

            document = existingDocument;
            isNewDocument = false;

            _logger.LogWarning(
                "Retrying incomplete document {DocumentId} for tenant {TenantId} because it has no chunks.",
                document.DocumentId,
                document.TenantId);
        }
        else
        {
            document = new Document
            {
                TenantId = request.TenantId,
                Title = request.Title.Trim(),
                SourceUrl = NormalizeOptionalValue(request.SourceUrl),
                ContentHash = contentHash,
                IngestedAt = DateTime.UtcNow
            };
            isNewDocument = true;
        }

        IReadOnlyList<Chunk> chunks = _chunkingService.ChunkDocument(document, request.Content);

        if (chunks.Count == 0)
            throw new InvalidOperationException("Chunking produced no chunks for the document.");

        IReadOnlyList<float[]> embeddings = await _embeddingService.EmbedBatchAsync(
            chunks.Select(c => c.TextContent).ToArray(),
            cancellationToken);

        if (embeddings.Count != chunks.Count)
            throw new InvalidOperationException($"Embedding count {embeddings.Count} does not match chunk count {chunks.Count}.");

        for (int i = 0; i < chunks.Count; i++)
        {
            chunks[i].Embedding = new Vector(embeddings[i]);
        }

        if (isNewDocument)
            _dbContext.Documents.Add(document);

        _dbContext.Chunks.AddRange(chunks);
        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsConcurrentDuplicateViolation(exception, isNewDocument))
        {
            _dbContext.ChangeTracker.Clear();

            var winner = await _dbContext.Documents
                .AsNoTracking()
                .Where(candidate =>
                    candidate.TenantId == request.TenantId &&
                    candidate.ContentHash == contentHash)
                .Select(candidate => new
                {
                    candidate.DocumentId,
                    ChunkCount = candidate.Chunks.Count
                })
                .SingleOrDefaultAsync(cancellationToken);

            if (winner is null || winner.ChunkCount == 0)
                throw;

            _logger.LogInformation(
                "Resolved concurrent duplicate document ingestion for tenant {TenantId}. Existing document: {DocumentId}.",
                request.TenantId,
                winner.DocumentId);

            return new DocumentIngestionResult(winner.DocumentId, winner.ChunkCount, true);
        }

        _logger.LogInformation(
            "Ingested document {DocumentId} for tenant {TenantId} with {ChunkCount} chunk(s).",
            document.DocumentId,
            document.TenantId,
            chunks.Count);

        return new DocumentIngestionResult(document.DocumentId, chunks.Count, false);
    }

    private static void ValidateRequest(DocumentIngestionRequest request)
    {
        if (request is null)
            throw new ArgumentNullException(nameof(request));

        if (request.TenantId == Guid.Empty)
            throw new ArgumentException("TenantId cannot be empty.", nameof(request));

        if (string.IsNullOrWhiteSpace(request.Title))
            throw new ArgumentException("Title cannot be empty.", nameof(request));

        if (request.Title.Trim().Length > MaxTitleLength)
            throw new ArgumentException($"Title cannot exceed {MaxTitleLength} characters.", nameof(request));

        if (!string.IsNullOrWhiteSpace(request.SourceUrl) && request.SourceUrl.Trim().Length > MaxSourceUrlLength)
            throw new ArgumentException($"SourceUrl cannot exceed {MaxSourceUrlLength} characters.", nameof(request));

        if (string.IsNullOrWhiteSpace(request.Content))
            throw new ArgumentException("Content cannot be empty.", nameof(request));
    }

    private static string? NormalizeOptionalValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return value.Trim();
    }

    private static string ComputeSha256Hash(string content)
    {
        byte[] contentBytes = Encoding.UTF8.GetBytes(content);
        byte[] hashBytes = SHA256.HashData(contentBytes);

        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    private static bool IsConcurrentDuplicateViolation(
        DbUpdateException exception,
        bool isNewDocument)
    {
        if (exception.InnerException is not PostgresException postgresException ||
            postgresException.SqlState != PostgresErrorCodes.UniqueViolation)
        {
            return false;
        }

        string expectedConstraint = isNewDocument
            ? DocumentContentHashConstraint
            : ChunkIndexConstraint;

        return string.Equals(
            postgresException.ConstraintName,
            expectedConstraint,
            StringComparison.Ordinal);
    }
}
