namespace SemanticRagSearch.Api.RateLimiting;

public static class TenantRateLimitingApplicationBuilderExtensions
{
    public static IApplicationBuilder UseTenantRateLimiting(
        this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<TenantRateLimitMiddleware>();
    }
}
