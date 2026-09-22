using Microsoft.Extensions.Options;

namespace SemanticRagSearch.Api.RateLimiting;

public static class RateLimitingServiceCollectionExtensions
{
    public static IServiceCollection AddTenantRateLimiting(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services
            .AddOptions<RateLimitingOptions>()
            .Bind(configuration.GetSection(RateLimitingOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<
            IValidateOptions<RateLimitingOptions>,
            RateLimitingOptionsValidator>();
        services.AddSingleton<IRedisConnectionProvider, RedisConnectionProvider>();
        services.AddSingleton<ITenantRateLimiter>(serviceProvider =>
            new RedisTenantRateLimiter(
                serviceProvider.GetRequiredService<IRedisConnectionProvider>(),
                serviceProvider.GetRequiredService<IOptions<RateLimitingOptions>>()));

        return services;
    }
}
