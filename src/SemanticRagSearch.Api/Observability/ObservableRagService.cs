using System.Diagnostics;
using System.Runtime.CompilerServices;
using SemanticRagSearch.Api.Rag;

namespace SemanticRagSearch.Api.Observability;

public sealed class ObservableRagService(IRagService inner) : IRagService
{
    public async Task<RagResponse> AskAsync(
        RagRequest request,
        CancellationToken cancellationToken = default)
    {
        const string operation = "rag.ask";
        long started = Stopwatch.GetTimestamp();
        Activity? activity = RagTelemetry.StartOperation(operation);

        try
        {
            RagResponse result = await inner.AskAsync(request, cancellationToken);
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

    public async IAsyncEnumerable<RagStreamEvent> AskStreamAsync(
        RagRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        const string operation = "rag.stream";
        long started = Stopwatch.GetTimestamp();
        Activity? activity = RagTelemetry.StartOperation(operation);
        string outcome = "cancelled";
        bool recordedFirstToken = false;
        IAsyncEnumerator<RagStreamEvent>? enumerator = null;

        try
        {
            try
            {
                enumerator = inner
                    .AskStreamAsync(request, cancellationToken)
                    .GetAsyncEnumerator(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                outcome = "cancelled";
                throw;
            }
            catch
            {
                outcome = "error";
                throw;
            }

            while (true)
            {
                bool hasNext;
                try
                {
                    hasNext = await enumerator.MoveNextAsync();
                }
                catch (OperationCanceledException)
                {
                    outcome = "cancelled";
                    throw;
                }
                catch
                {
                    outcome = "error";
                    throw;
                }

                if (!hasNext)
                    break;

                RagStreamEvent item = enumerator.Current;
                if (!recordedFirstToken && !string.IsNullOrEmpty(item.Text))
                {
                    RagTelemetry.RecordFirstToken(operation, started);
                    recordedFirstToken = true;
                }

                yield return item;
            }

            outcome = "success";
        }
        finally
        {
            try
            {
                if (enumerator is not null)
                    await enumerator.DisposeAsync();
            }
            catch (OperationCanceledException)
            {
                outcome = "cancelled";
                throw;
            }
            catch
            {
                outcome = "error";
                throw;
            }
            finally
            {
                RagTelemetry.CompleteOperation(activity, operation, outcome, started);
            }
        }
    }
}
