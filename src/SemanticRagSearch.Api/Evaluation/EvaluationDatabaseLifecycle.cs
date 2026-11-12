namespace SemanticRagSearch.Api.Evaluation;

public static class EvaluationDatabaseLifecycle
{
    public static async Task RunAsync(Func<CancellationToken, Task> create, Func<CancellationToken, Task> work,
        Func<CancellationToken, Task> drop, Action<Exception> reportCleanupFailure, CancellationToken cancellationToken)
    {
        Exception? originalFailure = null;
        try
        {
            await create(cancellationToken);
            await work(cancellationToken);
        }
        catch (Exception exception)
        {
            originalFailure = exception;
            throw;
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try
            {
                await drop(cleanup.Token);
            }
            catch (Exception exception)
            {
                reportCleanupFailure(exception);
                if (originalFailure is null)
                    throw;
            }
        }
    }
}
