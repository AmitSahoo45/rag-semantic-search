namespace SemanticRagSearch.Api.Resilience;

internal sealed class OllamaRequestDeadline : IDisposable
{
    private readonly CancellationTokenSource _timeoutCancellation = new();
    private readonly CancellationTokenSource _requestCancellation;
    private readonly ITimer _timer;

    public OllamaRequestDeadline(
        TimeProvider timeProvider,
        TimeSpan timeout,
        CancellationToken callerCancellationToken)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);

        _requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            callerCancellationToken,
            _timeoutCancellation.Token);
        _timer = timeProvider.CreateTimer(
            static state => Cancel((CancellationTokenSource)state!),
            _timeoutCancellation,
            timeout,
            Timeout.InfiniteTimeSpan);
    }

    public CancellationToken Token => _requestCancellation.Token;

    public void Dispose()
    {
        _timer.Dispose();
        _requestCancellation.Dispose();
        _timeoutCancellation.Dispose();
    }

    private static void Cancel(CancellationTokenSource cancellation)
    {
        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }
}
