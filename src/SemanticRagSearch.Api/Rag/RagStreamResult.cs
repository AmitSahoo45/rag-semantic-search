using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using SemanticRagSearch.Api.Errors;

namespace SemanticRagSearch.Api.Rag;

internal sealed class RagStreamResult(
    IAsyncEnumerable<RagStreamEvent> events,
    CancellationToken cancellationToken) : IResult
{
    public async Task ExecuteAsync(HttpContext httpContext)
    {
        await using IAsyncEnumerator<RagStreamEvent> enumerator =
            events.GetAsyncEnumerator(cancellationToken);

        bool hasFirstEvent;
        try
        {
            hasFirstEvent = await enumerator.MoveNextAsync();
        }
        catch (ArgumentException exception)
        {
            await Results.BadRequest(new ApiErrorResponse(exception.Message))
                .ExecuteAsync(httpContext);
            return;
        }
        catch (KeyNotFoundException exception)
        {
            await Results.NotFound(new ApiErrorResponse(exception.Message))
                .ExecuteAsync(httpContext);
            return;
        }

        IAsyncEnumerable<SseItem<object>> sseEvents = hasFirstEvent
            ? ContinueAsync(
                enumerator,
                enumerator.Current,
                httpContext,
                cancellationToken)
            : EmptyAsync();

        await TypedResults.ServerSentEvents(sseEvents).ExecuteAsync(httpContext);
    }

    private static async IAsyncEnumerable<SseItem<object>> ContinueAsync(
        IAsyncEnumerator<RagStreamEvent> enumerator,
        RagStreamEvent firstEvent,
        HttpContext httpContext,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return new SseItem<object>(firstEvent);

        while (true)
        {
            bool hasNext = false;
            bool wasCanceled = false;
            Exception? failure = null;

            try
            {
                hasNext = await enumerator.MoveNextAsync();
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested ||
                      httpContext.RequestAborted.IsCancellationRequested)
            {
                wasCanceled = true;
            }
            catch (Exception exception)
            {
                failure = exception;
            }

            if (wasCanceled || !hasNext && failure is null)
                yield break;

            if (failure is not null)
            {
                ApiError error = ApiErrorMapping.From(failure);
                ILogger<RagStreamResult> logger = httpContext.RequestServices
                    .GetRequiredService<ILogger<RagStreamResult>>();
                ApiErrorMapping.Log(logger, failure, httpContext);
                yield return new SseItem<object>(
                    new ApiErrorResponse(error.Message),
                    "error");
                yield break;
            }

            yield return new SseItem<object>(enumerator.Current);
        }
    }

    private static async IAsyncEnumerable<SseItem<object>> EmptyAsync()
    {
        await Task.CompletedTask;
        yield break;
    }
}
