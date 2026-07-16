namespace SemanticRagSearch.Api.Rag;

public interface IRagService
{
    Task<RagResponse> AskAsync(
        RagRequest request,
        CancellationToken cancellationToken = default);
}
