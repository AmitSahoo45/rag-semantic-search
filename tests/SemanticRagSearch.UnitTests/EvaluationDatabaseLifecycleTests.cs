using SemanticRagSearch.Api.Evaluation;

namespace SemanticRagSearch.UnitTests;

public sealed class EvaluationDatabaseLifecycleTests
{
    [Fact]
    public async Task RunAsync_WhenCreationAcknowledgementFails_StillAttemptsCleanupWithFreshToken()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        bool dropped = false;
        bool worked = false;
        var failure = new OperationCanceledException(cancelled.Token);
        var actual = await Assert.ThrowsAsync<OperationCanceledException>(() => EvaluationDatabaseLifecycle.RunAsync(
            _ => throw failure,
            _ => { worked = true; return Task.CompletedTask; },
            token => { Assert.False(token.IsCancellationRequested); dropped = true; return Task.CompletedTask; },
            _ => Assert.Fail("Cleanup should succeed."), cancelled.Token));
        Assert.Same(failure, actual);
        Assert.True(dropped);
        Assert.False(worked);
    }

    [Fact]
    public async Task RunAsync_WhenWorkAndCleanupFail_PreservesOriginalAndReportsCleanupFailure()
    {
        var workFailure = new InvalidOperationException("embedding failed");
        var dropFailure = new IOException("cleanup connection failed");
        Exception? reported = null;
        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() => EvaluationDatabaseLifecycle.RunAsync(
            _ => Task.CompletedTask, _ => throw workFailure, _ => throw dropFailure,
            exception => reported = exception, CancellationToken.None));
        Assert.Same(workFailure, actual);
        Assert.Same(dropFailure, reported);
    }

    [Fact]
    public async Task RunAsync_WhenOnlyCleanupFails_FailsTheRunAndReportsTheFailure()
    {
        var failure = new IOException("cleanup failed");
        Exception? reported = null;
        var actual = await Assert.ThrowsAsync<IOException>(() => EvaluationDatabaseLifecycle.RunAsync(
            _ => Task.CompletedTask, _ => Task.CompletedTask, _ => throw failure,
            exception => reported = exception, CancellationToken.None));
        Assert.Same(failure, actual);
        Assert.Same(failure, reported);
    }

    [Fact]
    public async Task RunAsync_WhenSuccessful_CreatesRunsAndDropsInOrder()
    {
        var events = new List<string>();
        await EvaluationDatabaseLifecycle.RunAsync(
            _ => { events.Add("create"); return Task.CompletedTask; },
            _ => { events.Add("work"); return Task.CompletedTask; },
            _ => { events.Add("drop"); return Task.CompletedTask; },
            _ => Assert.Fail("Cleanup should succeed."), CancellationToken.None);
        Assert.Equal(new[] { "create", "work", "drop" }, events);
    }
}
