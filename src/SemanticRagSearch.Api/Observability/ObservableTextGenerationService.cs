using System.Diagnostics;
using System.Runtime.CompilerServices;
using SemanticRagSearch.Api.Generation;

namespace SemanticRagSearch.Api.Observability;

public sealed class ObservableTextGenerationService(ITextGenerationService inner)
    : ITextGenerationService
{
    public async Task<string> GenerateAsync(
        string systemPrompt,
        string userPrompt,
        CancellationToken cancellationToken = default)
    {
        const string operation = "generation.complete";
        long started = Stopwatch.GetTimestamp();
        Activity? activity = RagTelemetry.StartOperation(operation);

        try
        {
            string result = await inner.GenerateAsync(systemPrompt, userPrompt, cancellationToken);
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

    public async IAsyncEnumerable<string> GenerateStreamAsync(
        string systemPrompt,
        string userPrompt,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        const string operation = "generation.stream";
        long started = Stopwatch.GetTimestamp();
        Activity? activity = RagTelemetry.StartOperation(operation);
        string outcome = "cancelled";
        bool recordedFirstToken = false;
        IAsyncEnumerator<string>? enumerator = null;

        try
        {
            try
            {
                enumerator = inner
                    .GenerateStreamAsync(systemPrompt, userPrompt, cancellationToken)
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

                string fragment = enumerator.Current;
                if (!recordedFirstToken && fragment.Length > 0)
                {
                    RagTelemetry.RecordFirstToken(operation, started);
                    recordedFirstToken = true;
                }

                yield return fragment;
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
