namespace SemanticRagSearch.Api.RateLimiting;

public sealed class RateLimitBackendUnavailableException : Exception
{
    public RateLimitBackendUnavailableException(string message)
        : base(message)
    {
    }

    public RateLimitBackendUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
