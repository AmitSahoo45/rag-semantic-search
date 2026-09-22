namespace SemanticRagSearch.Api.RateLimiting;

public readonly record struct RateLimitDecision
{
    private RateLimitDecision(bool isAllowed, int retryAfterSeconds)
    {
        IsAllowed = isAllowed;
        RetryAfterSeconds = retryAfterSeconds;
    }

    public bool IsAllowed { get; }
    public int RetryAfterSeconds { get; }

    public static RateLimitDecision Allowed { get; } = new(true, 0);

    public static RateLimitDecision Denied(int retryAfterSeconds)
    {
        if (retryAfterSeconds < 1)
            throw new ArgumentOutOfRangeException(nameof(retryAfterSeconds));

        return new RateLimitDecision(false, retryAfterSeconds);
    }
}
