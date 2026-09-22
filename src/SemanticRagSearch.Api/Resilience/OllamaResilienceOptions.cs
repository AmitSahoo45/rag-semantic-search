namespace SemanticRagSearch.Api.Resilience;

public sealed class OllamaResilienceOptions
{
    public const string SectionName = "App:OllamaResilience";

    public TimeSpan AttemptTimeout { get; set; } = TimeSpan.FromMinutes(2);

    public TimeSpan TotalRequestTimeout { get; set; } = TimeSpan.FromMinutes(3);

    public TimeSpan StreamingIdleTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public double CircuitBreakerFailureRatio { get; set; } = 0.5;

    public int CircuitBreakerMinimumThroughput { get; set; } = 4;

    public TimeSpan CircuitBreakerSamplingDuration { get; set; } = TimeSpan.FromMinutes(4);

    public TimeSpan CircuitBreakerBreakDuration { get; set; } = TimeSpan.FromSeconds(30);
}
