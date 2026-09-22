namespace SemanticRagSearch.Api.RateLimiting;

public enum RateLimitBucket
{
    Documents,
    Search,
    Ask,
    AskStream
}
