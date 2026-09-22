using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Polly.CircuitBreaker;
using Polly.Timeout;
using SemanticRagSearch.Api.Configuration;
using SemanticRagSearch.Api.Generation;
using SemanticRagSearch.Api.Ollama;
using SemanticRagSearch.Api.Resilience;

namespace SemanticRagSearch.UnitTests;

public class OllamaTextGenerationServiceTests
{
    [Fact]
    public async Task GenerateAsync_PostsNonStreamingChatRequestAndReturnsTrimmedContent()
    {
        string? requestJson = null;
        OllamaTextGenerationService service = CreateService(async (request, cancellationToken) =>
        {
            requestJson = await request.Content!.ReadAsStringAsync(cancellationToken);
            return JsonResponse(
                """{"message":{"role":"assistant","content":"  grounded answer  "},"done":true}""");
        });

        string answer = await service.GenerateAsync("system instructions", "user question");

        Assert.Equal("grounded answer", answer);
        using JsonDocument request = JsonDocument.Parse(requestJson!);
        Assert.Equal("llama3.2", request.RootElement.GetProperty("model").GetString());
        Assert.False(request.RootElement.GetProperty("stream").GetBoolean());
        JsonElement messages = request.RootElement.GetProperty("messages");
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Equal("system instructions", messages[0].GetProperty("content").GetString());
        Assert.Equal("user", messages[1].GetProperty("role").GetString());
        Assert.Equal("user question", messages[1].GetProperty("content").GetString());
    }

    [Fact]
    public async Task GenerateStreamAsync_PostsStreamingChatRequestAndYieldsOrderedFragments()
    {
        string? requestJson = null;
        OllamaTextGenerationService service = CreateService(async (request, cancellationToken) =>
        {
            requestJson = await request.Content!.ReadAsStringAsync(cancellationToken);
            return NdjsonResponse(
                """
                {"message":{"role":"assistant","content":"Grounded"},"done":false}
                {"message":{"role":"assistant","content":" answer"},"done":false}
                {"message":{"role":"assistant","content":""},"done":true}
                """);
        });

        List<string> fragments = await CollectAsync(
            service.GenerateStreamAsync("system instructions", "user question"));

        Assert.Equal(["Grounded", " answer"], fragments);
        using JsonDocument request = JsonDocument.Parse(requestJson!);
        Assert.Equal("llama3.2", request.RootElement.GetProperty("model").GetString());
        Assert.True(request.RootElement.GetProperty("stream").GetBoolean());
        Assert.Equal(
            "system instructions",
            request.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
        Assert.Equal(
            "user question",
            request.RootElement.GetProperty("messages")[1].GetProperty("content").GetString());
    }

    [Fact]
    public async Task GenerateStreamAsync_CompletesWithoutFragmentsForMetadataOnlyResponse()
    {
        OllamaTextGenerationService service = CreateService(
            (_, _) => Task.FromResult(NdjsonResponse(
                """{"message":{"role":"assistant","content":""},"done":true}""")));

        List<string> fragments = await CollectAsync(
            service.GenerateStreamAsync("system instructions", "user question"));

        Assert.Empty(fragments);
    }

    [Theory]
    [InlineData(" ", "user question")]
    [InlineData("system instructions", "\t")]
    public async Task GenerateStreamAsync_RejectsBlankPrompts(string systemPrompt, string userPrompt)
    {
        OllamaTextGenerationService service = CreateService(
            (_, _) => throw new InvalidOperationException("HTTP should not be called."));

        await Assert.ThrowsAsync<ArgumentException>(
            () => CollectAsync(service.GenerateStreamAsync(systemPrompt, userPrompt)));
    }

    [Theory]
    [InlineData(" ", "user question")]
    [InlineData("system instructions", "\t")]
    public async Task GenerateAsync_RejectsBlankPrompts(string systemPrompt, string userPrompt)
    {
        OllamaTextGenerationService service = CreateService(
            (_, _) => throw new InvalidOperationException("HTTP should not be called."));

        await Assert.ThrowsAsync<ArgumentException>(
            () => service.GenerateAsync(systemPrompt, userPrompt));
    }

    [Fact]
    public async Task GenerateStreamAsync_ClassifiesHttpFailureWithoutProviderBody()
    {
        OllamaTextGenerationService service = CreateService(
            (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("secret provider body")
            }));

        OllamaProviderException exception = await Assert.ThrowsAsync<OllamaProviderException>(
            () => CollectAsync(service.GenerateStreamAsync("system", "user")));

        Assert.Equal(OllamaFailureKind.InvalidResponse, exception.FailureKind);
        Assert.Equal(HttpStatusCode.InternalServerError, exception.UpstreamStatusCode);
        Assert.DoesNotContain("secret provider body", exception.Message);
    }

    [Fact]
    public async Task GenerateAsync_ClassifiesHttpFailureWithoutProviderBody()
    {
        OllamaTextGenerationService service = CreateService(
            (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway)
            {
                Content = new StringContent("secret provider body")
            }));

        OllamaProviderException exception = await Assert.ThrowsAsync<OllamaProviderException>(
            () => service.GenerateAsync("system", "user"));

        Assert.Equal(OllamaFailureKind.InvalidResponse, exception.FailureKind);
        Assert.Equal(HttpStatusCode.BadGateway, exception.UpstreamStatusCode);
        Assert.DoesNotContain("secret provider body", exception.Message);
    }

    [Fact]
    public async Task GenerateStreamAsync_ThrowsForInBandOllamaError()
    {
        OllamaTextGenerationService service = CreateService(
            (_, _) => Task.FromResult(NdjsonResponse(
                """
                {"message":{"role":"assistant","content":"partial"},"done":false}
                {"error":"generation failed"}
                """)));

        OllamaProviderException exception = await Assert.ThrowsAsync<OllamaProviderException>(
            () => CollectAsync(service.GenerateStreamAsync("system", "user")));

        Assert.Equal(OllamaFailureKind.InvalidResponse, exception.FailureKind);
        Assert.DoesNotContain("generation failed", exception.Message);
    }

    [Fact]
    public async Task GenerateStreamAsync_ThrowsForEmptyInBandOllamaError()
    {
        OllamaTextGenerationService service = CreateService(
            (_, _) => Task.FromResult(NdjsonResponse("""{"error":""}""")));

        OllamaProviderException exception = await Assert.ThrowsAsync<OllamaProviderException>(
            () => CollectAsync(service.GenerateStreamAsync("system", "user")));

        Assert.Equal(OllamaFailureKind.InvalidResponse, exception.FailureKind);
    }

    [Fact]
    public async Task GenerateStreamAsync_WrapsMalformedNdjson()
    {
        OllamaTextGenerationService service = CreateService(
            (_, _) => Task.FromResult(NdjsonResponse("{not-json}")));

        OllamaProviderException exception = await Assert.ThrowsAsync<OllamaProviderException>(
            () => CollectAsync(service.GenerateStreamAsync("system", "user")));

        Assert.Equal(OllamaFailureKind.InvalidResponse, exception.FailureKind);
        Assert.IsType<JsonException>(exception.InnerException);
    }

    [Fact]
    public async Task GenerateAsync_ThrowsWhenResponseHasNoContent()
    {
        OllamaTextGenerationService service = CreateService(
            (_, _) => Task.FromResult(JsonResponse(
                """{"message":{"role":"assistant","content":" "},"done":true}""")));

        OllamaProviderException exception = await Assert.ThrowsAsync<OllamaProviderException>(
            () => service.GenerateAsync("system", "user"));

        Assert.Equal(OllamaFailureKind.InvalidResponse, exception.FailureKind);
        Assert.Equal("Ollama did not return a chat response.", exception.Message);
    }

    [Fact]
    public async Task GenerateAsync_ClassifiesMalformedJsonAsInvalidResponse()
    {
        OllamaTextGenerationService service = CreateService(
            (_, _) => Task.FromResult(JsonResponse("{not-json}")));

        OllamaProviderException exception = await Assert.ThrowsAsync<OllamaProviderException>(
            () => service.GenerateAsync("system", "user"));

        Assert.Equal(OllamaFailureKind.InvalidResponse, exception.FailureKind);
        Assert.IsType<JsonException>(exception.InnerException);
    }

    [Fact]
    public async Task GenerateAsync_ClassifiesInBandErrorWithoutProviderBody()
    {
        OllamaTextGenerationService service = CreateService(
            (_, _) => Task.FromResult(JsonResponse(
                """{"error":"secret provider body"}""")));

        OllamaProviderException exception = await Assert.ThrowsAsync<OllamaProviderException>(
            () => service.GenerateAsync("system", "user"));

        Assert.Equal(OllamaFailureKind.InvalidResponse, exception.FailureKind);
        Assert.DoesNotContain("secret provider body", exception.Message);
    }

    [Fact]
    public async Task GenerateAsync_ClassifiesNetworkFailureAsUnavailable()
    {
        OllamaTextGenerationService service = CreateService(
            (_, _) => throw new HttpRequestException("connection refused"));

        OllamaProviderException exception = await Assert.ThrowsAsync<OllamaProviderException>(
            () => service.GenerateAsync("system", "user"));

        Assert.Equal(OllamaFailureKind.Unavailable, exception.FailureKind);
        Assert.IsType<HttpRequestException>(exception.InnerException);
    }

    [Fact]
    public async Task GenerateAsync_ClassifiesProviderCancellationAsTimeout()
    {
        OllamaTextGenerationService service = CreateService(
            (_, _) => throw new OperationCanceledException(
                "provider timeout",
                new TimeoutException(),
                CancellationToken.None));

        OllamaProviderException exception = await Assert.ThrowsAsync<OllamaProviderException>(
            () => service.GenerateAsync("system", "user"));

        Assert.Equal(OllamaFailureKind.Timeout, exception.FailureKind);
        Assert.IsType<OperationCanceledException>(exception.InnerException);
    }

    [Fact]
    public async Task GenerateAsync_ClassifiesResilienceTimeoutAsTimeout()
    {
        OllamaTextGenerationService service = CreateService(
            (_, _) => throw new TimeoutRejectedException("attempt timed out"));

        OllamaProviderException exception = await Assert.ThrowsAsync<OllamaProviderException>(
            () => service.GenerateAsync("system", "user"));

        Assert.Equal(OllamaFailureKind.Timeout, exception.FailureKind);
        Assert.IsType<TimeoutRejectedException>(exception.InnerException);
    }

    [Fact]
    public async Task GenerateAsync_ClassifiesOpenCircuitAsUnavailable()
    {
        OllamaTextGenerationService service = CreateService(
            (_, _) => throw new BrokenCircuitException("circuit is open"));

        OllamaProviderException exception = await Assert.ThrowsAsync<OllamaProviderException>(
            () => service.GenerateAsync("system", "user"));

        Assert.Equal(OllamaFailureKind.Unavailable, exception.FailureKind);
        Assert.IsType<BrokenCircuitException>(exception.InnerException);
    }

    [Fact]
    public async Task GenerateStreamAsync_MapsStalledReadToTimeoutAndDisposesStream()
    {
        var timeProvider = new ManualTimeProvider();
        int requestCount = 0;
        var responseStream = new PrefixThenBlockingStream(
            Encoding.UTF8.GetBytes(
                "{\"message\":{\"role\":\"assistant\",\"content\":\"first\"},\"done\":false}\n"));
        OllamaTextGenerationService service = CreateService(
            (_, _) =>
            {
                requestCount++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(responseStream)
                });
            },
            new OllamaResilienceOptions
            {
                StreamingIdleTimeout = TimeSpan.FromSeconds(5)
            },
            timeProvider);

        await using IAsyncEnumerator<string> stream = service
            .GenerateStreamAsync("system", "user")
            .GetAsyncEnumerator();
        Assert.True(await stream.MoveNextAsync());
        Assert.Equal("first", stream.Current);

        Task<bool> stalledRead = stream.MoveNextAsync().AsTask();
        await responseStream.BlockingReadStarted.WaitAsync(TimeSpan.FromSeconds(5));
        timeProvider.Advance(TimeSpan.FromSeconds(5));

        OllamaProviderException exception =
            await Assert.ThrowsAsync<OllamaProviderException>(() => stalledRead);
        Assert.Equal(OllamaFailureKind.Timeout, exception.FailureKind);
        Assert.Equal(1, requestCount);
        Assert.True(responseStream.IsDisposed);
    }

    [Fact]
    public async Task GenerateAsync_MapsStalledResponseBodyToTimeout()
    {
        var timeProvider = new ManualTimeProvider();
        var responseStream = new BlockingTrackingStream();
        OllamaTextGenerationService service = CreateService(
            (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(responseStream)
            }),
            new OllamaResilienceOptions
            {
                AttemptTimeout = TimeSpan.FromSeconds(4),
                TotalRequestTimeout = TimeSpan.FromSeconds(5)
            },
            timeProvider);

        Task<string> generation = service.GenerateAsync("system", "user");
        await responseStream.ReadStarted.WaitAsync(TimeSpan.FromSeconds(5));
        timeProvider.Advance(TimeSpan.FromSeconds(5));

        OllamaProviderException exception =
            await Assert.ThrowsAsync<OllamaProviderException>(() => generation);
        Assert.Equal(OllamaFailureKind.Timeout, exception.FailureKind);
        Assert.True(responseStream.IsDisposed);
    }

    [Fact]
    public async Task GenerateAsync_PreservesCallerCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        OllamaTextGenerationService service = CreateService(
            (_, token) => Task.FromCanceled<HttpResponseMessage>(token));

        OperationCanceledException exception =
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => service.GenerateAsync("system", "user", cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.IsNotType<OllamaProviderException>(exception);
    }

    [Fact]
    public async Task GenerateStreamAsync_ClassifiesResponseIoFailureAsUnavailable()
    {
        OllamaTextGenerationService service = CreateService(
            (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new ThrowingReadStream())
            }));

        OllamaProviderException exception = await Assert.ThrowsAsync<OllamaProviderException>(
            () => CollectAsync(service.GenerateStreamAsync("system", "user")));

        Assert.Equal(OllamaFailureKind.Unavailable, exception.FailureKind);
        Assert.IsType<IOException>(exception.InnerException);
    }

    [Fact]
    public async Task GenerateStreamAsync_ClassifiesEofBeforeDoneAsInvalidResponse()
    {
        OllamaTextGenerationService service = CreateService(
            (_, _) => Task.FromResult(NdjsonResponse(
                """{"message":{"role":"assistant","content":"partial"},"done":false}""")));

        OllamaProviderException exception = await Assert.ThrowsAsync<OllamaProviderException>(
            () => CollectAsync(service.GenerateStreamAsync("system", "user")));

        Assert.Equal(OllamaFailureKind.InvalidResponse, exception.FailureKind);
    }

    [Fact]
    public async Task GenerateStreamAsync_CancelsActiveReadAndDisposesResponseStream()
    {
        var responseStream = new BlockingTrackingStream();
        OllamaTextGenerationService service = CreateService(
            (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(responseStream)
            }));
        using var cancellation = new CancellationTokenSource();

        Task<List<string>> collection = CollectAsync(
            service.GenerateStreamAsync("system", "user", cancellation.Token));
        await responseStream.ReadStarted.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        OperationCanceledException exception =
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => collection);

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.True(responseStream.ReadCancellationToken.CanBeCanceled);
        Assert.True(responseStream.ReadCancellationToken.IsCancellationRequested);
        Assert.True(responseStream.IsDisposed);
    }

    [Fact]
    public async Task GenerateStreamAsync_DisposesStreamWhenConsumerStopsEarly()
    {
        var responseStream = new TrackingMemoryStream(
            Encoding.UTF8.GetBytes(
                """
                {"message":{"role":"assistant","content":"first"},"done":false}
                {"message":{"role":"assistant","content":"second"},"done":false}
                """));
        OllamaTextGenerationService service = CreateService(
            (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(responseStream)
            }));

        await foreach (string fragment in service.GenerateStreamAsync("system", "user"))
        {
            Assert.Equal("first", fragment);
            break;
        }

        Assert.True(responseStream.IsDisposed);
    }

    private static async Task<List<string>> CollectAsync(IAsyncEnumerable<string> fragments)
    {
        var result = new List<string>();
        await foreach (string fragment in fragments)
            result.Add(fragment);

        return result;
    }

    private static OllamaTextGenerationService CreateService(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler,
        OllamaResilienceOptions? resilienceOptions = null,
        TimeProvider? timeProvider = null)
    {
        var httpClient = new HttpClient(new TestHttpMessageHandler(handler))
        {
            BaseAddress = new Uri("http://localhost:11434")
        };

        return new OllamaTextGenerationService(
            new TestHttpClientFactory(httpClient),
            Options.Create(new OllamaSettings { ChatModel = "llama3.2" }),
            NullLogger<OllamaTextGenerationService>.Instance,
            Options.Create(resilienceOptions ?? new OllamaResilienceOptions()),
            timeProvider ?? TimeProvider.System);
    }

    private static HttpResponseMessage JsonResponse(string json)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    private static HttpResponseMessage NdjsonResponse(string ndjson)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(ndjson, Encoding.UTF8, "application/x-ndjson")
        };
    }

    private sealed class TestHttpClientFactory(HttpClient httpClient) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            Assert.Equal("Ollama", name);
            return httpClient;
        }
    }

    private sealed class TestHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/api/chat", request.RequestUri?.AbsolutePath);
            return handler(request, cancellationToken);
        }
    }

    private sealed class ThrowingReadStream : MemoryStream
    {
        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            return ValueTask.FromException<int>(
                new IOException("response stream interrupted"));
        }

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            return Task.FromException<int>(
                new IOException("response stream interrupted"));
        }
    }

    private sealed class BlockingTrackingStream : Stream
    {
        private readonly TaskCompletionSource _readStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task ReadStarted => _readStarted.Task;

        public CancellationToken ReadCancellationToken { get; private set; }

        public bool IsDisposed { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            ReadCancellationToken = cancellationToken;
            _readStarted.TrySetResult();
            return new ValueTask<int>(WaitForCancellationAsync(cancellationToken));
        }

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            ReadCancellationToken = cancellationToken;
            _readStarted.TrySetResult();
            return WaitForCancellationAsync(cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            IsDisposed = true;
            base.Dispose(disposing);
        }

        public override ValueTask DisposeAsync()
        {
            IsDisposed = true;
            return base.DisposeAsync();
        }

        public override void Flush()
        {
            throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        private static async Task<int> WaitForCancellationAsync(
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }

    private sealed class TrackingMemoryStream(byte[] buffer) : MemoryStream(buffer)
    {
        public bool IsDisposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            IsDisposed = true;
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            IsDisposed = true;
            await base.DisposeAsync();
        }
    }

    private sealed class PrefixThenBlockingStream(byte[] prefix) : Stream
    {
        private readonly TaskCompletionSource _blockingReadStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _position;

        public Task BlockingReadStarted => _blockingReadStarted.Task;

        public bool IsDisposed { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (_position < prefix.Length)
            {
                int bytesToCopy = Math.Min(buffer.Length, prefix.Length - _position);
                prefix.AsMemory(_position, bytesToCopy).CopyTo(buffer);
                _position += bytesToCopy;
                return ValueTask.FromResult(bytesToCopy);
            }

            _blockingReadStarted.TrySetResult();
            return new ValueTask<int>(WaitForCancellationAsync(cancellationToken));
        }

        protected override void Dispose(bool disposing)
        {
            IsDisposed = true;
            base.Dispose(disposing);
        }

        public override ValueTask DisposeAsync()
        {
            IsDisposed = true;
            return base.DisposeAsync();
        }

        public override void Flush() => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        private static async Task<int> WaitForCancellationAsync(
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly object _sync = new();
        private readonly List<ManualTimer> _timers = [];
        private long _timestamp;

        public override long GetTimestamp()
        {
            lock (_sync)
                return _timestamp;
        }

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state, dueTime, period);
            lock (_sync)
                _timers.Add(timer);

            return timer;
        }

        public void Advance(TimeSpan duration)
        {
            List<(TimerCallback Callback, object? State)> callbacks = [];
            lock (_sync)
            {
                _timestamp += duration.Ticks;
                foreach (ManualTimer timer in _timers.ToArray())
                {
                    if (timer.TryFire(_timestamp, out TimerCallback? callback, out object? state))
                        callbacks.Add((callback, state));
                }
            }

            foreach ((TimerCallback callback, object? state) in callbacks)
                callback(state);
        }

        private sealed class ManualTimer : ITimer
        {
            private readonly ManualTimeProvider _owner;
            private readonly TimerCallback _callback;
            private readonly object? _state;
            private long _dueTimestamp;
            private TimeSpan _period;
            private bool _disposed;

            public ManualTimer(
                ManualTimeProvider owner,
                TimerCallback callback,
                object? state,
                TimeSpan dueTime,
                TimeSpan period)
            {
                _owner = owner;
                _callback = callback;
                _state = state;
                Change(dueTime, period);
            }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (_owner._sync)
                {
                    if (_disposed)
                        return false;

                    _dueTimestamp = dueTime == Timeout.InfiniteTimeSpan
                        ? long.MaxValue
                        : checked(_owner._timestamp + dueTime.Ticks);
                    _period = period;
                    return true;
                }
            }

            public void Dispose()
            {
                lock (_owner._sync)
                    _disposed = true;
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }

            public bool TryFire(
                long timestamp,
                out TimerCallback callback,
                out object? state)
            {
                callback = _callback;
                state = _state;
                if (_disposed || timestamp < _dueTimestamp)
                    return false;

                _dueTimestamp = _period == Timeout.InfiniteTimeSpan
                    ? long.MaxValue
                    : checked(timestamp + _period.Ticks);
                return true;
            }
        }
    }
}
