using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace SemanticRagSearch.Api.RateLimiting;

internal sealed class RateLimitingOptionsValidator : IValidateOptions<RateLimitingOptions>
{
    private const int MaximumBucketValue = 1_000_000;
    private const int MaximumRefillPeriodSeconds = 86_400;
    private const int MaximumStateTtlSeconds = 604_800;

    public ValidateOptionsResult Validate(string? name, RateLimitingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();

        if (options.Enabled)
        {
            if (string.IsNullOrWhiteSpace(options.RedisConnectionString))
            {
                failures.Add("RedisConnectionString is required when rate limiting is enabled.");
            }
            else
            {
                try
                {
                    _ = ConfigurationOptions.Parse(options.RedisConnectionString);
                }
                catch (Exception exception) when (exception is ArgumentException or FormatException)
                {
                    failures.Add("RedisConnectionString is not a valid Redis configuration string.");
                }
            }
        }

        if (options.RedisConnectTimeoutMilliseconds is < 50 or > 60_000)
        {
            failures.Add("RedisConnectTimeoutMilliseconds must be between 50 and 60000.");
        }

        if (options.StateTtlSeconds is < 1 or > MaximumStateTtlSeconds)
        {
            failures.Add($"StateTtlSeconds must be between 1 and {MaximumStateTtlSeconds}.");
        }

        ValidateBucket("Documents", options.Documents, options.StateTtlSeconds, failures);
        ValidateBucket("Search", options.Search, options.StateTtlSeconds, failures);
        ValidateBucket("Ask", options.Ask, options.StateTtlSeconds, failures);
        ValidateBucket("AskStream", options.AskStream, options.StateTtlSeconds, failures);

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    private static void ValidateBucket(
        string name,
        RateLimitBucketOptions? bucket,
        int stateTtlSeconds,
        ICollection<string> failures)
    {
        if (bucket is null)
        {
            failures.Add($"{name} rate-limit settings are required.");
            return;
        }

        if (bucket.Capacity is < 1 or > MaximumBucketValue)
            failures.Add($"{name}.Capacity must be between 1 and {MaximumBucketValue}.");

        if (bucket.RefillTokens is < 1 or > MaximumBucketValue)
            failures.Add($"{name}.RefillTokens must be between 1 and {MaximumBucketValue}.");

        if (bucket.RefillPeriodSeconds is < 1 or > MaximumRefillPeriodSeconds)
        {
            failures.Add(
                $"{name}.RefillPeriodSeconds must be between 1 and {MaximumRefillPeriodSeconds}.");
        }

        if (bucket.Capacity < 1 || bucket.RefillTokens < 1 || bucket.RefillPeriodSeconds < 1)
            return;

        long refillNumerator = (long)bucket.Capacity * bucket.RefillPeriodSeconds;
        long refillHorizonSeconds =
            (refillNumerator + bucket.RefillTokens - 1) / bucket.RefillTokens;
        if (stateTtlSeconds < refillHorizonSeconds)
        {
            failures.Add(
                $"StateTtlSeconds must be at least {refillHorizonSeconds} for the {name} bucket to refill fully before its state expires.");
        }
    }
}
