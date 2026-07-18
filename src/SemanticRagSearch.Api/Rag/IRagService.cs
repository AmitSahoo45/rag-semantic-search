namespace SemanticRagSearch.Api.Rag;

public interface IRagService
{
    Task<RagResponse> AskAsync(
        RagRequest request,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<RagStreamEvent> AskStreamAsync(
        RagRequest request,
        CancellationToken cancellationToken = default);
}
