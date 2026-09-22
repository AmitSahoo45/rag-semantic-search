using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SemanticRagSearch.Api.RateLimiting;

namespace SemanticRagSearch.UnitTests;

public sealed class RateLimitingOptionsTests
{
    [Fact]
    public void AddTenantRateLimiting_BindsValidBucketConfiguration()
    {
        IConfiguration configuration = CreateConfiguration(
            new Dictionary<string, string?>
            {
                ["App:RateLimiting:RedisConnectionString"] = "redis.internal:6380",
                ["App:RateLimiting:Documents:Capacity"] = "7",
                ["App:RateLimiting:Documents:RefillTokens"] = "2",
                ["App:RateLimiting:Documents:RefillPeriodSeconds"] = "5",
                ["App:RateLimiting:StateTtlSeconds"] = "60"
            });
        var services = new ServiceCollection();

        services.AddTenantRateLimiting(configuration);

        using ServiceProvider provider = services.BuildServiceProvider();
        RateLimitingOptions options = provider
            .GetRequiredService<IOptions<RateLimitingOptions>>()
            .Value;
        Assert.Equal("redis.internal:6380", options.RedisConnectionString);
        Assert.Equal(7, options.Documents.Capacity);
        Assert.Equal(2, options.Documents.RefillTokens);
        Assert.Equal(5, options.Documents.RefillPeriodSeconds);
    }

    [Fact]
    public void AddTenantRateLimiting_RejectsTtlShorterThanFullRefillHorizon()
    {
        IConfiguration configuration = CreateConfiguration(
            new Dictionary<string, string?>
            {
                ["App:RateLimiting:Documents:Capacity"] = "10",
                ["App:RateLimiting:Documents:RefillTokens"] = "1",
                ["App:RateLimiting:Documents:RefillPeriodSeconds"] = "10",
                ["App:RateLimiting:StateTtlSeconds"] = "99"
            });
        var services = new ServiceCollection();
        services.AddTenantRateLimiting(configuration);
        using ServiceProvider provider = services.BuildServiceProvider();

        OptionsValidationException exception = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<RateLimitingOptions>>().Value);

        Assert.Contains(
            "StateTtlSeconds must be at least 100",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Capacity", "0")]
    [InlineData("RefillTokens", "0")]
    [InlineData("RefillPeriodSeconds", "0")]
    public void AddTenantRateLimiting_RejectsNonPositiveBucketValues(
        string property,
        string value)
    {
        IConfiguration configuration = CreateConfiguration(
            new Dictionary<string, string?>
            {
                [$"App:RateLimiting:Search:{property}"] = value
            });
        var services = new ServiceCollection();
        services.AddTenantRateLimiting(configuration);
        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<RateLimitingOptions>>().Value);
    }

    [Fact]
    public void AddTenantRateLimiting_RejectsEmptyRedisConnectionWhenEnabled()
    {
        IConfiguration configuration = CreateConfiguration(
            new Dictionary<string, string?>
            {
                ["App:RateLimiting:Enabled"] = "true",
                ["App:RateLimiting:RedisConnectionString"] = ""
            });
        var services = new ServiceCollection();
        services.AddTenantRateLimiting(configuration);
        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<RateLimitingOptions>>().Value);
    }

    [Fact]
    public void DisabledRateLimiter_CanBeResolvedAndSynchronouslyDisposedWithoutRedis()
    {
        IConfiguration configuration = CreateConfiguration(
            new Dictionary<string, string?>
            {
                ["App:RateLimiting:Enabled"] = "false",
                ["App:RateLimiting:RedisConnectionString"] = ""
            });
        var services = new ServiceCollection();
        services.AddTenantRateLimiting(configuration);
        ServiceProvider provider = services.BuildServiceProvider();
        _ = provider.GetRequiredService<ITenantRateLimiter>();

        Exception? exception = Record.Exception(provider.Dispose);

        Assert.Null(exception);
    }

    private static IConfiguration CreateConfiguration(
        IReadOnlyDictionary<string, string?> values)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
    }
}
