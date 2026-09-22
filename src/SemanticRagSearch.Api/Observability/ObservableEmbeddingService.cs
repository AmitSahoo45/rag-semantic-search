using System.Diagnostics;
using SemanticRagSearch.Api.Embedding;

namespace SemanticRagSearch.Api.Observability;

public sealed class ObservableEmbeddingService(IEmbeddingService inner) : IEmbeddingService
{
    public async Task<float[]> EmbedAsync(
        string text,
        CancellationToken cancellationToken = default)
    {
        const string operation = "embedding.single";
        long started = Stopwatch.GetTimestamp();
        Activity? activity = RagTelemetry.StartOperation(operation);

        try
        {
            float[] result = await inner.EmbedAsync(text, cancellationToken);
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

    public async Task<IReadOnlyList<float[]>> EmbedBatchAsync(
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken = default)
    {
        const string operation = "embedding.batch";
        long started = Stopwatch.GetTimestamp();
        Activity? activity = RagTelemetry.StartOperation(operation);

        try
        {
            IReadOnlyList<float[]> result = await inner.EmbedBatchAsync(texts, cancellationToken);
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
