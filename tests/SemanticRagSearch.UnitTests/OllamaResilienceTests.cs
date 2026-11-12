using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Polly.CircuitBreaker;
using Polly.Timeout;
using SemanticRagSearch.Api.Resilience;

namespace SemanticRagSearch.UnitTests;

public class OllamaResilienceTests
{
    [Fact]
    public async Task RequestTimeout_StopsPostWithoutReplayingIt()
    {
        var timeProvider = new ManualTimeProvider();
        var handler = new ControlledHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        await using ServiceProvider services = CreateServices(
            timeProvider,
            handler,
            new Dictionary<string, string?>
            {
                [$"{OllamaResilienceOptions.SectionName}:AttemptTimeout"] = "00:00:05",
                [$"{OllamaResilienceOptions.SectionName}:TotalRequestTimeout"] = "00:00:20",
                [$"{OllamaResilienceOptions.SectionName}:CircuitBreakerSamplingDuration"] = "00:00:10"
            });
        HttpClient client = services.GetRequiredService<IHttpClientFactory>().CreateClient("Ollama");

        Task<HttpResponseMessage> request = client.PostAsync("/api/chat", new StringContent("{}"));
        await handler.RequestStarted.WaitAsync(TimeSpan.FromSeconds(5));
        timeProvider.Advance(TimeSpan.FromSeconds(5));

        await Assert.ThrowsAsync<TimeoutRejectedException>(() => request);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task FailureThreshold_OpensSharedCircuitWithoutSendingAnotherRequest()
    {
        var timeProvider = new ManualTimeProvider();
        var handler = new ControlledHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        await using ServiceProvider services = CreateServices(
            timeProvider,
            handler,
            CircuitTestConfiguration());
        IHttpClientFactory factory = services.GetRequiredService<IHttpClientFactory>();

        using HttpResponseMessage first = await factory.CreateClient("Ollama")
            .PostAsync("/api/embed", new StringContent("{}"));
        using HttpResponseMessage second = await factory.CreateClient("Ollama")
            .PostAsync("/api/embed", new StringContent("{}"));

        await Assert.ThrowsAsync<BrokenCircuitException>(
            () => factory.CreateClient("Ollama").PostAsync("/api/embed", new StringContent("{}")));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, first.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, second.StatusCode);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task BreakDurationElapsed_AllowsProbeAndClosesRecoveredCircuit()
    {
        var timeProvider = new ManualTimeProvider();
        int responseNumber = 0;
        var handler = new ControlledHandler((_, _) =>
        {
            HttpStatusCode statusCode = Interlocked.Increment(ref responseNumber) <= 2
                ? HttpStatusCode.ServiceUnavailable
                : HttpStatusCode.OK;
            return Task.FromResult(new HttpResponseMessage(statusCode));
        });
        await using ServiceProvider services = CreateServices(
            timeProvider,
            handler,
            CircuitTestConfiguration());
        HttpClient client = services.GetRequiredService<IHttpClientFactory>().CreateClient("Ollama");

        using HttpResponseMessage first = await client.PostAsync("/api/embed", new StringContent("{}"));
        using HttpResponseMessage second = await client.PostAsync("/api/embed", new StringContent("{}"));
        await Assert.ThrowsAsync<BrokenCircuitException>(
            () => client.PostAsync("/api/embed", new StringContent("{}")));

        timeProvider.Advance(TimeSpan.FromSeconds(11));

        using HttpResponseMessage recoveryProbe = await client.PostAsync(
            "/api/embed",
            new StringContent("{}"));
        using HttpResponseMessage afterRecovery = await client.PostAsync(
            "/api/embed",
            new StringContent("{}"));

        Assert.Equal(HttpStatusCode.OK, recoveryProbe.StatusCode);
        Assert.Equal(HttpStatusCode.OK, afterRecovery.StatusCode);
        Assert.Equal(4, handler.RequestCount);
    }

    [Fact]
    public async Task CallerCancellation_IsPreservedAndDoesNotBecomePipelineTimeout()
    {
        var timeProvider = new ManualTimeProvider();
        var handler = new ControlledHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        await using ServiceProvider services = CreateServices(
            timeProvider,
            handler,
            CircuitTestConfiguration());
        HttpClient client = services.GetRequiredService<IHttpClientFactory>().CreateClient("Ollama");
        using var cancellation = new CancellationTokenSource();

        Task<HttpResponseMessage> request = client.PostAsync(
            "/api/chat",
            new StringContent("{}"),
            cancellation.Token);
        await handler.RequestStarted.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        OperationCanceledException exception =
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        Assert.IsNotType<TimeoutRejectedException>(exception);
        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public void InvalidTimeoutConfiguration_FailsOptionsValidation()
    {
        var timeProvider = new ManualTimeProvider();
        var handler = new ControlledHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        using ServiceProvider services = CreateServices(
            timeProvider,
            handler,
            new Dictionary<string, string?>
            {
                [$"{OllamaResilienceOptions.SectionName}:AttemptTimeout"] = "00:00:20",
                [$"{OllamaResilienceOptions.SectionName}:TotalRequestTimeout"] = "00:00:10"
            });

        Action resolveOptions = () =>
            _ = services.GetRequiredService<
                Microsoft.Extensions.Options.IOptions<OllamaResilienceOptions>>().Value;

        Assert.Throws<Microsoft.Extensions.Options.OptionsValidationException>(resolveOptions);
    }

    [Fact]
    public void EqualAttemptAndTotalTimeouts_FailOptionsValidation()
    {
        var timeProvider = new ManualTimeProvider();
        var handler = new ControlledHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        using ServiceProvider services = CreateServices(
            timeProvider,
            handler,
            new Dictionary<string, string?>
            {
                [$"{OllamaResilienceOptions.SectionName}:AttemptTimeout"] = "00:00:10",
                [$"{OllamaResilienceOptions.SectionName}:TotalRequestTimeout"] = "00:00:10"
            });

        Action resolveOptions = () =>
            _ = services.GetRequiredService<
                Microsoft.Extensions.Options.IOptions<OllamaResilienceOptions>>().Value;

        Assert.Throws<Microsoft.Extensions.Options.OptionsValidationException>(resolveOptions);
    }

    [Theory]
    [InlineData("AttemptTimeout", "00:00:00.009")]
    [InlineData("AttemptTimeout", "1.00:00:00.001")]
    [InlineData("TotalRequestTimeout", "00:00:00.009")]
    [InlineData("TotalRequestTimeout", "1.00:00:00.001")]
    [InlineData("CircuitBreakerSamplingDuration", "00:00:00.499")]
    [InlineData("CircuitBreakerSamplingDuration", "1.00:00:00.001")]
    [InlineData("CircuitBreakerBreakDuration", "00:00:00.499")]
    [InlineData("CircuitBreakerBreakDuration", "1.00:00:00.001")]
    public void DurationOutsidePollyRange_FailsOptionsValidation(string key, string value)
    {
        var timeProvider = new ManualTimeProvider();
        var handler = new ControlledHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        using ServiceProvider services = CreateServices(
            timeProvider,
            handler,
            new Dictionary<string, string?>
            {
                [$"{OllamaResilienceOptions.SectionName}:{key}"] = value
            });

        Action resolveOptions = () =>
            _ = services.GetRequiredService<
                Microsoft.Extensions.Options.IOptions<OllamaResilienceOptions>>().Value;

        Assert.Throws<Microsoft.Extensions.Options.OptionsValidationException>(resolveOptions);
    }

    [Fact]
    public void DurationsAtPollyRangeBoundaries_PassOptionsValidation()
    {
        var timeProvider = new ManualTimeProvider();
        var handler = new ControlledHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        using ServiceProvider services = CreateServices(
            timeProvider,
            handler,
            new Dictionary<string, string?>
            {
                [$"{OllamaResilienceOptions.SectionName}:AttemptTimeout"] = "00:00:00.010",
                [$"{OllamaResilienceOptions.SectionName}:TotalRequestTimeout"] = "1.00:00:00",
                [$"{OllamaResilienceOptions.SectionName}:CircuitBreakerSamplingDuration"] = "00:00:00.500",
                [$"{OllamaResilienceOptions.SectionName}:CircuitBreakerBreakDuration"] = "1.00:00:00"
            });

        OllamaResilienceOptions options = services.GetRequiredService<
            Microsoft.Extensions.Options.IOptions<OllamaResilienceOptions>>().Value;
        HttpClient client = services.GetRequiredService<IHttpClientFactory>().CreateClient("Ollama");

        Assert.Equal(TimeSpan.FromMilliseconds(10), options.AttemptTimeout);
        Assert.Equal(TimeSpan.FromDays(1), options.TotalRequestTimeout);
        Assert.NotNull(client);
    }

    [Fact]
    public async Task CancellationNotRequestedByCaller_CountsAsFailureAndOpensCircuit()
    {
        var timeProvider = new ManualTimeProvider();
        var handler = new ControlledHandler((_, _) =>
            Task.FromException<HttpResponseMessage>(new OperationCanceledException()));
        await using ServiceProvider services = CreateServices(
            timeProvider,
            handler,
            CircuitTestConfiguration());
        HttpClient client = services.GetRequiredService<IHttpClientFactory>().CreateClient("Ollama");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.PostAsync("/api/embed", new StringContent("{}")));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.PostAsync("/api/embed", new StringContent("{}")));

        await Assert.ThrowsAsync<BrokenCircuitException>(
            () => client.PostAsync("/api/embed", new StringContent("{}")));
        Assert.Equal(2, handler.RequestCount);
    }

    private static Dictionary<string, string?> CircuitTestConfiguration()
    {
        return new Dictionary<string, string?>
        {
            [$"{OllamaResilienceOptions.SectionName}:AttemptTimeout"] = "00:00:05",
            [$"{OllamaResilienceOptions.SectionName}:TotalRequestTimeout"] = "00:00:20",
            [$"{OllamaResilienceOptions.SectionName}:CircuitBreakerFailureRatio"] = "1.0",
            [$"{OllamaResilienceOptions.SectionName}:CircuitBreakerMinimumThroughput"] = "2",
            [$"{OllamaResilienceOptions.SectionName}:CircuitBreakerSamplingDuration"] = "00:00:10",
            [$"{OllamaResilienceOptions.SectionName}:CircuitBreakerBreakDuration"] = "00:00:10"
        };
    }

    private static ServiceProvider CreateServices(
        TimeProvider timeProvider,
        HttpMessageHandler handler,
        IReadOnlyDictionary<string, string?> configurationValues)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configurationValues)
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(timeProvider);
        services.AddHttpClient("Ollama", client =>
            {
                client.BaseAddress = new Uri("http://localhost:11434");
            })
            .ConfigurePrimaryHttpMessageHandler(() => handler)
            .AddOllamaResilience(configuration);

        return services.BuildServiceProvider();
    }

    private sealed class ControlledHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> sendAsync)
        : HttpMessageHandler
    {
        private readonly TaskCompletionSource _requestStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int RequestCount { get; private set; }

        public Task RequestStarted => _requestStarted.Task;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            _requestStarted.TrySetResult();
            return sendAsync(request, cancellationToken);
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly object _sync = new();
        private readonly List<ManualTimer> _timers = [];
        private DateTimeOffset _utcNow = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        private long _timestamp;

        public override DateTimeOffset GetUtcNow()
        {
            lock (_sync)
                return _utcNow;
        }

        public override long GetTimestamp()
        {
            lock (_sync)
                return _timestamp;
        }

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

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
            if (duration < TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(duration));

            List<(TimerCallback Callback, object? State)> callbacks = [];
            lock (_sync)
            {
                _utcNow += duration;
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
