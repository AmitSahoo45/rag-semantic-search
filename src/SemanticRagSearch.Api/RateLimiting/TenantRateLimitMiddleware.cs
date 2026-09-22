using System.Security.Claims;
using Microsoft.Extensions.Options;

namespace SemanticRagSearch.Api.RateLimiting;

public sealed class TenantRateLimitMiddleware
{
    private const string TenantIdClaimType = "tenant_id";
    private readonly RequestDelegate _next;
    private readonly RateLimitingOptions _options;
    private readonly ILogger<TenantRateLimitMiddleware> _logger;

    public TenantRateLimitMiddleware(
        RequestDelegate next,
        IOptions<RateLimitingOptions> options,
        ILogger<TenantRateLimitMiddleware> logger)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task InvokeAsync(
        HttpContext context,
        ITenantRateLimiter rateLimiter)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(rateLimiter);

        if (!_options.Enabled || !TryGetBucket(context.Request, out RateLimitBucket bucket))
        {
            await _next(context);
            return;
        }

        string? tenantClaim = context.User.FindFirstValue(TenantIdClaimType);
        if (context.User.Identity?.IsAuthenticated != true
            || !Guid.TryParseExact(tenantClaim, "D", out Guid tenantId))
        {
            await WriteErrorAsync(
                context,
                StatusCodes.Status401Unauthorized,
                "An authenticated tenant is required.");
            return;
        }

        try
        {
            RateLimitDecision decision = await rateLimiter.TryAcquireAsync(
                tenantId,
                bucket,
                context.RequestAborted);
            if (!decision.IsAllowed)
            {
                context.Response.Headers.RetryAfter =
                    decision.RetryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
                await WriteErrorAsync(
                    context,
                    StatusCodes.Status429TooManyRequests,
                    "The rate limit for this endpoint has been exceeded.");
                return;
            }
        }
        catch (RateLimitBackendUnavailableException)
        {
            _logger.LogWarning(
                "The Redis rate-limit backend is unavailable; the request was rejected.");
            await WriteErrorAsync(
                context,
                StatusCodes.Status503ServiceUnavailable,
                "Rate limiting is temporarily unavailable.");
            return;
        }

        await _next(context);
    }

    private static bool TryGetBucket(
        HttpRequest request,
        out RateLimitBucket bucket)
    {
        bucket = default;
        if (!HttpMethods.IsPost(request.Method))
            return false;

        string path = request.Path.Value?.TrimEnd('/') ?? string.Empty;
        if (path.Equals("/api/v1/documents", StringComparison.OrdinalIgnoreCase))
        {
            bucket = RateLimitBucket.Documents;
            return true;
        }

        if (path.Equals("/api/v1/search", StringComparison.OrdinalIgnoreCase))
        {
            bucket = RateLimitBucket.Search;
            return true;
        }

        if (path.Equals("/api/v1/ask", StringComparison.OrdinalIgnoreCase))
        {
            bucket = RateLimitBucket.Ask;
            return true;
        }

        if (path.Equals("/api/v1/ask/stream", StringComparison.OrdinalIgnoreCase))
        {
            bucket = RateLimitBucket.AskStream;
            return true;
        }

        return false;
    }

    private static Task WriteErrorAsync(
        HttpContext context,
        int statusCode,
        string error)
    {
        context.Response.StatusCode = statusCode;
        return context.Response.WriteAsJsonAsync(
            new { error },
            context.RequestAborted);
    }
}
