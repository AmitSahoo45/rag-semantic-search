using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using Polly;

namespace SemanticRagSearch.Api.Resilience;

public static class OllamaResilienceExtensions
{
    private const string PipelineName = "ollama-resilience";

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
                static options => options.AttemptTimeout > TimeSpan.Zero,
                "Ollama resilience attempt timeout must be greater than zero.")
            .Validate(
                static options => options.TotalRequestTimeout >= options.AttemptTimeout,
                "Ollama resilience total request timeout must be at least the attempt timeout.")
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
                static options => options.CircuitBreakerSamplingDuration > TimeSpan.Zero,
                "Ollama resilience circuit breaker sampling duration must be greater than zero.")
            .Validate(
                static options => options.CircuitBreakerBreakDuration > TimeSpan.Zero,
                "Ollama resilience circuit breaker break duration must be greater than zero.")
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
                BreakDuration = options.CircuitBreakerBreakDuration
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
}
