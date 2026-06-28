namespace SemanticRagSearch.Api.Ingestion;

public interface IDocumentIngestionService
{
    Task<DocumentIngestionResult> IngestAsync(
        DocumentIngestionRequest request,
        CancellationToken cancellationToken = default);
}
