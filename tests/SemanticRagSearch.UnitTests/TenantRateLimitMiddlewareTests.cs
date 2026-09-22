using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SemanticRagSearch.Api.RateLimiting;

namespace SemanticRagSearch.UnitTests;

public sealed class TenantRateLimitMiddlewareTests
{
    private static readonly Guid TenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Theory]
    [InlineData("POST", "/api/v1/documents", RateLimitBucket.Documents)]
    [InlineData("POST", "/api/v1/search", RateLimitBucket.Search)]
    [InlineData("POST", "/api/v1/ask", RateLimitBucket.Ask)]
    [InlineData("POST", "/api/v1/ask/stream", RateLimitBucket.AskStream)]
    [InlineData("post", "/API/V1/SEARCH/", RateLimitBucket.Search)]
    public async Task InvokeAsync_UsesAuthenticatedTenantAndStableRouteBucket(
        string method,
        string path,
        RateLimitBucket expectedBucket)
    {
        var limiter = new RecordingRateLimiter(RateLimitDecision.Allowed);
        DefaultHttpContext context = CreateContext(method, path, TenantId);
        bool nextCalled = false;
        var middleware = CreateMiddleware(
            _ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            });

        await middleware.InvokeAsync(context, limiter);

        Assert.True(nextCalled);
        Assert.Equal(TenantId, limiter.TenantId);
        Assert.Equal(expectedBucket, limiter.Bucket);
        Assert.Equal(context.RequestAborted, limiter.CancellationToken);
    }

    [Theory]
    [InlineData("GET", "/api/v1/search")]
    [InlineData("POST", "/api/v1/search/suggestions")]
    [InlineData("POST", "/health")]
    public async Task InvokeAsync_DoesNotLimitMethodsOrRoutesOutsideStableBuckets(
        string method,
        string path)
    {
        var limiter = new RecordingRateLimiter(RateLimitDecision.Denied(30));
        DefaultHttpContext context = CreateContext(method, path, TenantId);
        bool nextCalled = false;
        var middleware = CreateMiddleware(
            _ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            });

        await middleware.InvokeAsync(context, limiter);

        Assert.True(nextCalled);
        Assert.Equal(0, limiter.CallCount);
    }

    [Fact]
    public async Task InvokeAsync_Returns429WithIntegerRetryAfterWhenBucketIsExhausted()
    {
        var limiter = new RecordingRateLimiter(RateLimitDecision.Denied(7));
        DefaultHttpContext context = CreateContext("POST", "/api/v1/ask", TenantId);
        var middleware = CreateMiddleware(_ => throw new InvalidOperationException("next must not run"));

        await middleware.InvokeAsync(context, limiter);

        Assert.Equal(StatusCodes.Status429TooManyRequests, context.Response.StatusCode);
        Assert.Equal("7", context.Response.Headers.RetryAfter);
        Assert.Equal(
            "The rate limit for this endpoint has been exceeded.",
            await ReadOnlyErrorAsync(context));
    }

    [Fact]
    public async Task InvokeAsync_ReturnsSanitized503WhenRedisIsUnavailable()
    {
        var limiter = new ThrowingRateLimiter(
            new RateLimitBackendUnavailableException("secret redis endpoint"));
        DefaultHttpContext context = CreateContext("POST", "/api/v1/documents", TenantId);
        var middleware = CreateMiddleware(_ => throw new InvalidOperationException("next must not run"));

        await middleware.InvokeAsync(context, limiter);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        string body = await ReadBodyAsync(context);
        Assert.Equal("Rate limiting is temporarily unavailable.", ReadError(body));
        Assert.DoesNotContain("secret redis endpoint", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvokeAsync_PropagatesRequestCancellation()
    {
        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();
        var limiter = new CancellationAwareRateLimiter();
        DefaultHttpContext context = CreateContext("POST", "/api/v1/search", TenantId);
        context.RequestAborted = cancellationSource.Token;
        var middleware = CreateMiddleware(_ => throw new InvalidOperationException("next must not run"));

        OperationCanceledException exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => middleware.InvokeAsync(context, limiter));

        Assert.Equal(cancellationSource.Token, exception.CancellationToken);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-guid")]
    public async Task InvokeAsync_RejectsMissingOrMalformedTenantClaim(string? tenantClaim)
    {
        DefaultHttpContext context = CreateContext("POST", "/api/v1/search", null);
        if (tenantClaim is not null)
        {
            context.User = new ClaimsPrincipal(
                new ClaimsIdentity([new Claim("tenant_id", tenantClaim)], "Test"));
        }

        var limiter = new RecordingRateLimiter(RateLimitDecision.Allowed);
        var middleware = CreateMiddleware(_ => throw new InvalidOperationException("next must not run"));

        await middleware.InvokeAsync(context, limiter);

        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        Assert.Equal("An authenticated tenant is required.", await ReadOnlyErrorAsync(context));
        Assert.Equal(0, limiter.CallCount);
    }

    [Fact]
    public async Task InvokeAsync_RejectsTenantClaimFromUnauthenticatedIdentity()
    {
        DefaultHttpContext context = CreateContext("POST", "/api/v1/search", null);
        context.User = new ClaimsPrincipal(
            new ClaimsIdentity([new Claim("tenant_id", TenantId.ToString("D"))]));
        var limiter = new RecordingRateLimiter(RateLimitDecision.Allowed);
        var middleware = CreateMiddleware(_ => throw new InvalidOperationException("next must not run"));

        await middleware.InvokeAsync(context, limiter);

        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        Assert.Equal("An authenticated tenant is required.", await ReadOnlyErrorAsync(context));
        Assert.Equal(0, limiter.CallCount);
    }

    [Fact]
    public async Task InvokeAsync_BypassesRedisWhenRateLimitingIsDisabled()
    {
        var limiter = new ThrowingRateLimiter(
            new InvalidOperationException("disabled middleware must not call Redis"));
        DefaultHttpContext context = CreateContext("POST", "/api/v1/search", TenantId);
        bool nextCalled = false;
        var middleware = CreateMiddleware(
            _ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            },
            new RateLimitingOptions { Enabled = false });

        await middleware.InvokeAsync(context, limiter);

        Assert.True(nextCalled);
    }

    private static TenantRateLimitMiddleware CreateMiddleware(
        RequestDelegate next,
        RateLimitingOptions? options = null)
    {
        return new TenantRateLimitMiddleware(
            next,
            Options.Create(options ?? new RateLimitingOptions()),
            NullLogger<TenantRateLimitMiddleware>.Instance);
    }

    private static DefaultHttpContext CreateContext(
        string method,
        string path,
        Guid? tenantId)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();

        if (tenantId.HasValue)
        {
            context.User = new ClaimsPrincipal(
                new ClaimsIdentity(
                    [new Claim("tenant_id", tenantId.Value.ToString("D"))],
                    "Test"));
        }

        return context;
    }

    private static async Task<string> ReadOnlyErrorAsync(DefaultHttpContext context)
    {
        string body = await ReadBodyAsync(context);
        using JsonDocument document = JsonDocument.Parse(body);
        Assert.Single(document.RootElement.EnumerateObject());
        return document.RootElement.GetProperty("error").GetString()!;
    }

    private static async Task<string> ReadBodyAsync(DefaultHttpContext context)
    {
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body, leaveOpen: true);
        return await reader.ReadToEndAsync();
    }

    private static string? ReadError(string body)
    {
        using JsonDocument document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("error").GetString();
    }

    private sealed class RecordingRateLimiter(RateLimitDecision decision) : ITenantRateLimiter
    {
        public int CallCount { get; private set; }
        public Guid TenantId { get; private set; }
        public RateLimitBucket Bucket { get; private set; }
        public CancellationToken CancellationToken { get; private set; }

        public ValueTask<RateLimitDecision> TryAcquireAsync(
            Guid tenantId,
            RateLimitBucket bucket,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            TenantId = tenantId;
            Bucket = bucket;
            CancellationToken = cancellationToken;
            return ValueTask.FromResult(decision);
        }
    }

    private sealed class ThrowingRateLimiter(Exception exception) : ITenantRateLimiter
    {
        public ValueTask<RateLimitDecision> TryAcquireAsync(
            Guid tenantId,
            RateLimitBucket bucket,
            CancellationToken cancellationToken = default)
        {
            return ValueTask.FromException<RateLimitDecision>(exception);
        }
    }

    private sealed class CancellationAwareRateLimiter : ITenantRateLimiter
    {
        public async ValueTask<RateLimitDecision> TryAcquireAsync(
            Guid tenantId,
            RateLimitBucket bucket,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return RateLimitDecision.Allowed;
        }
    }
}
