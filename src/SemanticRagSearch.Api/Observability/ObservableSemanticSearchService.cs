using System.Diagnostics;
using SemanticRagSearch.Api.Search;

namespace SemanticRagSearch.Api.Observability;

public sealed class ObservableSemanticSearchService(ISemanticSearchService inner)
    : ISemanticSearchService
{
    public async Task<SemanticSearchResponse> SearchAsync(
        SemanticSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        const string operation = "search";
        long started = Stopwatch.GetTimestamp();
        Activity? activity = RagTelemetry.StartOperation(operation);

        try
        {
            SemanticSearchResponse result = await inner.SearchAsync(request, cancellationToken);
            RagTelemetry.CompleteOperation(activity, operation, "success", started);
            return result;
        }
        catch (OperationCanceledException)
        {
            RagTelemetry.CompleteOperation(activity, operation, "cancelled", started);
            throw;
        }
        catch
        {
            RagTelemetry.CompleteOperation(activity, operation, "error", started);
            throw;
        }
    }
}
