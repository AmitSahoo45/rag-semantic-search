namespace SemanticRagSearch.Api.RateLimiting;

public interface ITenantRateLimiter
{
    ValueTask<RateLimitDecision> TryAcquireAsync(
        Guid tenantId,
        RateLimitBucket bucket,
        CancellationToken cancellationToken = default);
}
