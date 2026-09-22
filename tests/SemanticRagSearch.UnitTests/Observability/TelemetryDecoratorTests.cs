using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using SemanticRagSearch.Api.Embedding;
using SemanticRagSearch.Api.Generation;
using SemanticRagSearch.Api.Observability;
using SemanticRagSearch.Api.Rag;
using SemanticRagSearch.Api.Search;

namespace SemanticRagSearch.UnitTests.Observability;

public sealed class TelemetryDecoratorTests
{
    [Fact]
    public async Task EmbeddingSuccess_RecordsLatencyAndCallWithoutInputContent()
    {
        const string secretInput = "private document text 7d29";
        using var telemetry = new TelemetryCapture();
        var service = new ObservableEmbeddingService(
            new StubEmbeddingService(singleResult: [0.1f, 0.2f]));

        float[] result = await service.EmbedAsync(secretInput);

        Assert.Equal([0.1f, 0.2f], result);
        telemetry.AssertCompletedOperation("embedding.single", "success");
        Assert.DoesNotContain(secretInput, telemetry.AllExportedText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmbeddingBatchSuccess_UsesBoundedBatchOperationTag()
    {
        using var telemetry = new TelemetryCapture();
        var service = new ObservableEmbeddingService(
            new StubEmbeddingService(batchResult: [[0.3f], [0.4f]]));

        IReadOnlyList<float[]> result = await service.EmbedBatchAsync(["first", "second"]);

        Assert.Equal(2, result.Count);
        telemetry.AssertCompletedOperation("embedding.batch", "success");
    }

    [Fact]
    public async Task SearchFailure_RecordsFailureWithoutQueryTenantOrExceptionMessage()
    {
        const string secretQuery = "secret query c315";
        Guid tenantId = Guid.NewGuid();
        var expected = new InvalidOperationException("private provider response 952b");
        using var telemetry = new TelemetryCapture();
        var service = new ObservableSemanticSearchService(
            new StubSearchService((_, _) => Task.FromException<SemanticSearchResponse>(expected)));

        Exception actual = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SearchAsync(new SemanticSearchRequest(tenantId, secretQuery, 3, 0.4)));

        Assert.Same(expected, actual);
        telemetry.AssertCompletedOperation("search", "error", expectFailure: true);
        string exported = telemetry.AllExportedText();
        Assert.DoesNotContain(secretQuery, exported, StringComparison.Ordinal);
        Assert.DoesNotContain(tenantId.ToString(), exported, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(expected.Message, exported, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerationCancellation_RecordsCancelledFailureAndPreservesException()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var expected = new OperationCanceledException("private cancellation detail", cancellation.Token);
        using var telemetry = new TelemetryCapture();
        var service = new ObservableTextGenerationService(
            new StubGenerationService(
                generate: (_, _, _) => Task.FromException<string>(expected)));

        Exception actual = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.GenerateAsync("private system prompt", "private user prompt", cancellation.Token));

        Assert.Same(expected, actual);
        telemetry.AssertCompletedOperation("generation.complete", "cancelled", expectFailure: true);
        Assert.DoesNotContain(expected.Message, telemetry.AllExportedText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerationStream_RecordsFirstTokenOnceAfterFirstNonEmptyFragment()
    {
        const string secretPrompt = "secret prompt 3dd5";
        using var telemetry = new TelemetryCapture();
        var service = new ObservableTextGenerationService(
            new StubGenerationService(stream: (_, _, token) => Fragments(token)));

        var fragments = new List<string>();
        await foreach (string fragment in service.GenerateStreamAsync("system", secretPrompt))
            fragments.Add(fragment);

        Assert.Equal(["", "first private fragment", "second private fragment"], fragments);
        telemetry.AssertCompletedOperation("generation.stream", "success");
        MeasurementRecord firstToken = Assert.Single(
            telemetry.Measurements,
            measurement => measurement.InstrumentName == "rag.stream.first_token.duration" &&
                measurement.Tag("rag.operation") == "generation.stream");
        Assert.True(firstToken.Value >= 0);
        Assert.DoesNotContain(secretPrompt, telemetry.AllExportedText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerationStream_WhenEnumeratorCreationFails_RecordsError()
    {
        var expected = new InvalidOperationException("private enumerator creation failure");
        using var telemetry = new TelemetryCapture();
        var service = new ObservableTextGenerationService(
            new StubGenerationService(
                stream: (_, _, _) => new ThrowingAsyncEnumerable<string>(expected, false)));

        Exception actual = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (string _ in service.GenerateStreamAsync("system", "user"))
            {
            }
        });

        Assert.Same(expected, actual);
        telemetry.AssertCompletedOperation("generation.stream", "error", expectFailure: true);
        Assert.DoesNotContain(expected.Message, telemetry.AllExportedText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerationStream_WhenEnumeratorDisposalFails_RecordsError()
    {
        var expected = new InvalidOperationException("private enumerator disposal failure");
        using var telemetry = new TelemetryCapture();
        var service = new ObservableTextGenerationService(
            new StubGenerationService(
                stream: (_, _, _) => new ThrowingAsyncEnumerable<string>(expected, true)));

        Exception actual = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (string _ in service.GenerateStreamAsync("system", "user"))
            {
            }
        });

        Assert.Same(expected, actual);
        telemetry.AssertCompletedOperation("generation.stream", "error", expectFailure: true);
        Assert.DoesNotContain(expected.Message, telemetry.AllExportedText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RagAsk_RecordsRagOperationWithoutQuestionOrTenant()
    {
        const string secretQuestion = "private question 684b";
        Guid tenantId = Guid.NewGuid();
        var response = new RagResponse(
            tenantId,
            secretQuestion,
            3,
            0.4,
            "private answer",
            [],
            new RagExecutionMetadata(1, 2, null));
        using var telemetry = new TelemetryCapture();
        var service = new ObservableRagService(new StubRagService(response));

        RagResponse actual = await service.AskAsync(
            new RagRequest(tenantId, secretQuestion, 3, 0.4));

        Assert.Same(response, actual);
        telemetry.AssertCompletedOperation("rag.ask", "success");
        string exported = telemetry.AllExportedText();
        Assert.DoesNotContain(secretQuestion, exported, StringComparison.Ordinal);
        Assert.DoesNotContain(tenantId.ToString(), exported, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RagStream_RecordsFirstTextLatencyAndCompletion()
    {
        Guid tenantId = Guid.NewGuid();
        using var telemetry = new TelemetryCapture();
        var service = new ObservableRagService(
            new StubRagService(
                stream: token => RagEvents(token)));

        var events = new List<RagStreamEvent>();
        await foreach (RagStreamEvent item in service.AskStreamAsync(
            new RagRequest(tenantId, "private question", 3, 0.4)))
        {
            events.Add(item);
        }

        Assert.Equal(3, events.Count);
        telemetry.AssertCompletedOperation("rag.stream", "success");
        Assert.Single(
            telemetry.Measurements,
            measurement => measurement.InstrumentName == "rag.stream.first_token.duration" &&
                measurement.Tag("rag.operation") == "rag.stream");
    }

    [Fact]
    public async Task RagStream_WhenEnumeratorCreationFails_RecordsError()
    {
        var expected = new InvalidOperationException("private rag enumerator creation failure");
        using var telemetry = new TelemetryCapture();
        var service = new ObservableRagService(
            new StubRagService(
                stream: _ => new ThrowingAsyncEnumerable<RagStreamEvent>(expected, false)));

        Exception actual = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (RagStreamEvent _ in service.AskStreamAsync(
                new RagRequest(Guid.NewGuid(), "private", 3, 0.4)))
            {
            }
        });

        Assert.Same(expected, actual);
        telemetry.AssertCompletedOperation("rag.stream", "error", expectFailure: true);
    }

    [Fact]
    public async Task RagStream_WhenEnumeratorDisposalFails_RecordsError()
    {
        var expected = new InvalidOperationException("private rag enumerator disposal failure");
        using var telemetry = new TelemetryCapture();
        var service = new ObservableRagService(
            new StubRagService(
                stream: _ => new ThrowingAsyncEnumerable<RagStreamEvent>(expected, true)));

        Exception actual = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (RagStreamEvent _ in service.AskStreamAsync(
                new RagRequest(Guid.NewGuid(), "private", 3, 0.4)))
            {
            }
        });

        Assert.Same(expected, actual);
        telemetry.AssertCompletedOperation("rag.stream", "error", expectFailure: true);
    }

    private static async IAsyncEnumerable<string> Fragments(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        yield return "";
        await Task.Yield();
        yield return "first private fragment";
        yield return "second private fragment";
    }

    private static async IAsyncEnumerable<RagStreamEvent> RagEvents(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        yield return new RagStreamEvent(null, false, null, null);
        await Task.Yield();
        yield return new RagStreamEvent("private answer fragment", false, null, null);
        yield return new RagStreamEvent(null, true, [], new RagExecutionMetadata(1, 2, 1));
    }

    private sealed class StubEmbeddingService(
        float[]? singleResult = null,
        IReadOnlyList<float[]>? batchResult = null) : IEmbeddingService
    {
        public Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default) =>
            Task.FromResult(singleResult ?? [1f]);

        public Task<IReadOnlyList<float[]>> EmbedBatchAsync(
            IReadOnlyList<string> texts,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(batchResult ?? (IReadOnlyList<float[]>)[[1f]]);
    }

    private sealed class StubSearchService(
        Func<SemanticSearchRequest, CancellationToken, Task<SemanticSearchResponse>> search)
        : ISemanticSearchService
    {
        public Task<SemanticSearchResponse> SearchAsync(
            SemanticSearchRequest request,
            CancellationToken cancellationToken = default) => search(request, cancellationToken);
    }

    private sealed class StubGenerationService(
        Func<string, string, CancellationToken, Task<string>>? generate = null,
        Func<string, string, CancellationToken, IAsyncEnumerable<string>>? stream = null)
        : ITextGenerationService
    {
        public Task<string> GenerateAsync(
            string systemPrompt,
            string userPrompt,
            CancellationToken cancellationToken = default) =>
            generate?.Invoke(systemPrompt, userPrompt, cancellationToken) ?? Task.FromResult("answer");

        public IAsyncEnumerable<string> GenerateStreamAsync(
            string systemPrompt,
            string userPrompt,
            CancellationToken cancellationToken = default) =>
            stream?.Invoke(systemPrompt, userPrompt, cancellationToken) ?? Fragments(cancellationToken);
    }

    private sealed class StubRagService(
        RagResponse? response = null,
        Func<CancellationToken, IAsyncEnumerable<RagStreamEvent>>? stream = null) : IRagService
    {
        public Task<RagResponse> AskAsync(
            RagRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(response ?? new RagResponse(
                request.TenantId,
                request.Question,
                request.TopK ?? 3,
                request.MinSimilarity ?? 0.4,
                "answer",
                [],
                new RagExecutionMetadata(1, 2, null)));

        public IAsyncEnumerable<RagStreamEvent> AskStreamAsync(
            RagRequest request,
            CancellationToken cancellationToken = default) =>
            stream?.Invoke(cancellationToken) ?? RagEvents(cancellationToken);
    }

    private sealed class ThrowingAsyncEnumerable<T>(
        Exception exception,
        bool throwOnDispose) : IAsyncEnumerable<T>
    {
        public IAsyncEnumerator<T> GetAsyncEnumerator(
            CancellationToken cancellationToken = default)
        {
            if (!throwOnDispose)
                throw exception;

            return new ThrowingAsyncEnumerator<T>(exception);
        }
    }

    private sealed class ThrowingAsyncEnumerator<T>(Exception exception)
        : IAsyncEnumerator<T>
    {
        public T Current => default!;

        public ValueTask<bool> MoveNextAsync() => ValueTask.FromResult(false);

        public ValueTask DisposeAsync() => ValueTask.FromException(exception);
    }

    private sealed class TelemetryCapture : IDisposable
    {
        private readonly ActivityListener _activityListener;
        private readonly MeterListener _meterListener;

        public TelemetryCapture()
        {
            _activityListener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == "SemanticRagSearch",
                Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                    ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = activity => Activities.Add(activity)
            };
            ActivitySource.AddActivityListener(_activityListener);

            _meterListener = new MeterListener
            {
                InstrumentPublished = (instrument, listener) =>
                {
                    if (instrument.Meter.Name == "SemanticRagSearch")
                        listener.EnableMeasurementEvents(instrument);
                }
            };
            _meterListener.SetMeasurementEventCallback<long>(
                (instrument, measurement, tags, _) => AddMeasurement(instrument, measurement, tags));
            _meterListener.SetMeasurementEventCallback<double>(
                (instrument, measurement, tags, _) => AddMeasurement(instrument, measurement, tags));
            _meterListener.Start();
        }

        public ConcurrentBag<Activity> Activities { get; } = [];

        public ConcurrentBag<MeasurementRecord> Measurements { get; } = [];

        public void AssertCompletedOperation(
            string operation,
            string outcome,
            bool expectFailure = false)
        {
            Activity activity = Assert.Single(
                Activities,
                candidate => candidate.OperationName == operation);
            Assert.Equal(operation, activity.GetTagItem("rag.operation"));
            Assert.Equal(outcome, activity.GetTagItem("rag.outcome"));
            Assert.Equal(
                outcome == "success" ? ActivityStatusCode.Ok : ActivityStatusCode.Error,
                activity.Status);

            MeasurementRecord call = Assert.Single(
                Measurements,
                measurement => measurement.InstrumentName == "rag.operation.calls" &&
                    measurement.Tag("rag.operation") == operation);
            Assert.Equal(1, call.Value);
            Assert.Equal(outcome, call.Tag("rag.outcome"));

            MeasurementRecord duration = Assert.Single(
                Measurements,
                measurement => measurement.InstrumentName == "rag.operation.duration" &&
                    measurement.Tag("rag.operation") == operation);
            Assert.True(duration.Value >= 0);
            Assert.Equal(outcome, duration.Tag("rag.outcome"));

            Assert.Equal(
                expectFailure ? 1 : 0,
                Measurements.Count(measurement =>
                    measurement.InstrumentName == "rag.operation.failures" &&
                    measurement.Tag("rag.operation") == operation &&
                    measurement.Tag("rag.outcome") == outcome));
        }

        public string AllExportedText()
        {
            IEnumerable<string?> activityValues = Activities.SelectMany(activity =>
                activity.TagObjects.Select(tag => $"{tag.Key}={tag.Value}")
                    .Append(activity.DisplayName)
                    .Append(activity.StatusDescription));
            IEnumerable<string> measurementValues = Measurements.SelectMany(measurement =>
                measurement.Tags.Select(tag => $"{tag.Key}={tag.Value}"));

            return string.Join('|', activityValues.Concat(measurementValues));
        }

        public void Dispose()
        {
            _meterListener.Dispose();
            _activityListener.Dispose();
        }

        private void AddMeasurement<T>(
            Instrument instrument,
            T measurement,
            ReadOnlySpan<KeyValuePair<string, object?>> tags)
            where T : struct
        {
            Measurements.Add(new MeasurementRecord(
                instrument.Name,
                Convert.ToDouble(measurement),
                tags.ToArray()));
        }
    }

    private sealed record MeasurementRecord(
        string InstrumentName,
        double Value,
        IReadOnlyList<KeyValuePair<string, object?>> Tags)
    {
        public string? Tag(string name) =>
            Tags.FirstOrDefault(tag => tag.Key == name).Value?.ToString();
    }
}
