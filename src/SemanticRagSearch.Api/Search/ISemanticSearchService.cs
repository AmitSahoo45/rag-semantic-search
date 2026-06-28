namespace SemanticRagSearch.Api.Search;

public interface ISemanticSearchService
{
    Task<SemanticSearchResponse> SearchAsync(
        SemanticSearchRequest request,
        CancellationToken cancellationToken = default);
}
