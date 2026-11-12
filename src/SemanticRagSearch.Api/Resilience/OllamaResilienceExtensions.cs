using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using Polly;
using Polly.CircuitBreaker;

namespace SemanticRagSearch.Api.Resilience;

public static class OllamaResilienceExtensions
{
    private const string PipelineName = "ollama-resilience";

    // Polly rejects the pipeline at creation time when a duration falls outside these ranges, so they
    // are enforced at startup instead of surfacing as a failure on every provider call.
    private static readonly TimeSpan MinimumTimeout = TimeSpan.FromMilliseconds(10);
    private static readonly TimeSpan MinimumCircuitBreakerDuration = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan MaximumDuration = TimeSpan.FromDays(1);

    public static IHttpClientBuilder AddOllamaResilience(
        this IHttpClientBuilder builder,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configuration);

        builder.Services
            .AddOptions<OllamaResilienceOptions>()
            .Bind(configuration.GetSection(OllamaResilienceOptions.SectionName))
            .Validate(
                static options => IsWithinPollyRange(options.AttemptTimeout, MinimumTimeout),
                "Ollama resilience attempt timeout must be between 10 milliseconds and 1 day.")
            .Validate(
                static options => IsWithinPollyRange(options.TotalRequestTimeout, MinimumTimeout),
                "Ollama resilience total request timeout must be between 10 milliseconds and 1 day.")
            .Validate(
                static options => options.TotalRequestTimeout > options.AttemptTimeout,
                "Ollama resilience total request timeout must be greater than the attempt timeout.")
            .Validate(
                static options => options.StreamingIdleTimeout > TimeSpan.Zero,
                "Ollama resilience streaming idle timeout must be greater than zero.")
            .Validate(
                static options => options.CircuitBreakerFailureRatio is > 0 and <= 1,
                "Ollama resilience circuit breaker failure ratio must be greater than zero and at most one.")
            .Validate(
                static options => options.CircuitBreakerMinimumThroughput >= 2,
                "Ollama resilience circuit breaker minimum throughput must be at least two.")
            .Validate(
                static options => IsWithinPollyRange(
                    options.CircuitBreakerSamplingDuration,
                    MinimumCircuitBreakerDuration),
                "Ollama resilience circuit breaker sampling duration must be between 500 milliseconds and 1 day.")
            .Validate(
                static options => IsWithinPollyRange(
                    options.CircuitBreakerBreakDuration,
                    MinimumCircuitBreakerDuration),
                "Ollama resilience circuit breaker break duration must be between 500 milliseconds and 1 day.")
            .ValidateOnStart();

        builder.Services.TryAddSingleton(TimeProvider.System);

        builder.AddResilienceHandler(PipelineName, static (pipeline, context) =>
        {
            OllamaResilienceOptions options = context.ServiceProvider
                .GetRequiredService<IOptions<OllamaResilienceOptions>>()
                .Value;

            pipeline.TimeProvider = context.ServiceProvider.GetRequiredService<TimeProvider>();
            pipeline.AddTimeout(new HttpTimeoutStrategyOptions
            {
                Timeout = options.TotalRequestTimeout
            });
            pipeline.AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
            {
                FailureRatio = options.CircuitBreakerFailureRatio,
                MinimumThroughput = options.CircuitBreakerMinimumThroughput,
                SamplingDuration = options.CircuitBreakerSamplingDuration,
                BreakDuration = options.CircuitBreakerBreakDuration,
                ShouldHandle = static args => new ValueTask<bool>(
                    HttpClientResiliencePredicates.IsTransient(args.Outcome)
                    || IsCancellationNotRequestedByCaller(args))
            });
            pipeline.AddTimeout(new HttpTimeoutStrategyOptions
            {
                Timeout = options.AttemptTimeout
            });
        });

        builder.ConfigureHttpClient(static client =>
            client.Timeout = Timeout.InfiniteTimeSpan);

        return builder;
    }

    private static bool IsWithinPollyRange(TimeSpan value, TimeSpan minimum) =>
        value >= minimum && value <= MaximumDuration;

    // Polly counts every outcome the predicate does not handle as a success, so a cancellation that
    // did not come from the caller (for example a connection timeout) must count as a failure or it
    // would close a half-open circuit. Caller cancellation says nothing about provider health.
    private static bool IsCancellationNotRequestedByCaller(
        CircuitBreakerPredicateArguments<HttpResponseMessage> args) =>
        args.Outcome.Exception is OperationCanceledException
        && !args.Context.CancellationToken.IsCancellationRequested;
}
