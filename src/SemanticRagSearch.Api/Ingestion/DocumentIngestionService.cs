using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Pgvector;
using SemanticRagSearch.Api.Chunking;
using SemanticRagSearch.Api.Data;
using SemanticRagSearch.Api.Domain;
using SemanticRagSearch.Api.Embedding;
using SemanticRagSearch.Api.VectorStore;

namespace SemanticRagSearch.Api.Ingestion;

public class DocumentIngestionService : IDocumentIngestionService
{
    private const int MaxTitleLength = 300;
    private const int MaxSourceUrlLength = 2048;

    private readonly AppDbContext _dbContext;
    private readonly IChunkingService _chunkingService;
    private readonly IEmbeddingService _embeddingService;
    private readonly IVectorStoreService _vectorStoreService;
    private readonly ILogger<DocumentIngestionService> _logger;

    public DocumentIngestionService(
        AppDbContext dbContext,
        IChunkingService chunkingService,
        IEmbeddingService embeddingService,
        IVectorStoreService vectorStoreService,
        ILogger<DocumentIngestionService> logger)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _chunkingService = chunkingService ?? throw new ArgumentNullException(nameof(chunkingService));
        _embeddingService = embeddingService ?? throw new ArgumentNullException(nameof(embeddingService));
        _vectorStoreService = vectorStoreService ?? throw new ArgumentNullException(nameof(vectorStoreService));
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

        if (existingDocument is not null)
        {
            int existingChunkCount = await _dbContext.Chunks
                .CountAsync(c => c.DocumentId == existingDocument.DocumentId, cancellationToken);

            _logger.LogInformation(
                "Skipped duplicate document ingestion for tenant {TenantId}. Existing document: {DocumentId}.",
                request.TenantId,
                existingDocument.DocumentId);

            return new DocumentIngestionResult(existingDocument.DocumentId, existingChunkCount, true);
        }

        var document = new Document
        {
            TenantId = request.TenantId,
            Title = request.Title.Trim(),
            SourceUrl = NormalizeOptionalValue(request.SourceUrl),
            ContentHash = contentHash,
            IngestedAt = DateTime.UtcNow
        };

        _dbContext.Documents.Add(document);
        await _dbContext.SaveChangesAsync(cancellationToken);

        try
        {
            IReadOnlyList<Chunk> chunks = _chunkingService.ChunkDocument(document, request.Content);

            IReadOnlyList<float[]> embeddings = await _embeddingService.EmbedBatchAsync(
                chunks.Select(c => c.TextContent).ToArray(),
                cancellationToken);

            if (embeddings.Count != chunks.Count)
                throw new InvalidOperationException($"Embedding count {embeddings.Count} does not match chunk count {chunks.Count}.");

            for (int i = 0; i < chunks.Count; i++)
            {
                chunks[i].Embedding = new Vector(embeddings[i]);
            }

            await _vectorStoreService.StoreBatchAsync(chunks, cancellationToken);

            _logger.LogInformation(
                "Ingested document {DocumentId} for tenant {TenantId} with {ChunkCount} chunk(s).",
                document.DocumentId,
                document.TenantId,
                chunks.Count);

            return new DocumentIngestionResult(document.DocumentId, chunks.Count, false);
        }
        catch
        {
            await CleanupDocumentAsync(document.DocumentId);
            throw;
        }
    }

    private async Task CleanupDocumentAsync(Guid documentId)
    {
        try
        {
            await _vectorStoreService.DeleteByDocumentIdAsync(documentId);

            Document? document = await _dbContext.Documents.FindAsync(documentId);
            if (document is null)
                return;

            _dbContext.Documents.Remove(document);
            await _dbContext.SaveChangesAsync();

            _logger.LogWarning("Cleaned up document {DocumentId} after ingestion failure.", documentId);
        }
        catch (Exception cleanupException)
        {
            _logger.LogError(
                cleanupException,
                "Failed to clean up document {DocumentId} after ingestion failure.",
                documentId);
        }
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
}
