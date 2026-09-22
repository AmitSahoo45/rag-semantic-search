namespace SemanticRagSearch.Api.Ingestion;

public sealed record DocumentIngestionRequest(
    Guid TenantId,
    string Title,
    string? SourceUrl,
    string Content) : Authentication.ITenantRequest;
