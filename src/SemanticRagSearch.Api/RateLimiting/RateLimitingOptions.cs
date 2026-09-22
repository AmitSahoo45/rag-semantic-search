namespace SemanticRagSearch.Api.RateLimiting;

public sealed class RateLimitingOptions
{
    public const string SectionName = "App:RateLimiting";

    public bool Enabled { get; set; } = true;
    public string RedisConnectionString { get; set; } = "localhost:6379,abortConnect=false";
    public int RedisConnectTimeoutMilliseconds { get; set; } = 5_000;
    public int StateTtlSeconds { get; set; } = 3_600;

    public RateLimitBucketOptions Documents { get; set; } = new()
    {
        Capacity = 20,
        RefillTokens = 20,
        RefillPeriodSeconds = 60
    };

    public RateLimitBucketOptions Search { get; set; } = new()
    {
        Capacity = 60,
        RefillTokens = 60,
        RefillPeriodSeconds = 60
    };

    public RateLimitBucketOptions Ask { get; set; } = new()
    {
        Capacity = 20,
        RefillTokens = 20,
        RefillPeriodSeconds = 60
    };

    public RateLimitBucketOptions AskStream { get; set; } = new()
    {
        Capacity = 10,
        RefillTokens = 10,
        RefillPeriodSeconds = 60
    };

    public RateLimitBucketOptions GetBucket(RateLimitBucket bucket)
    {
        return bucket switch
        {
            RateLimitBucket.Documents => Documents,
            RateLimitBucket.Search => Search,
            RateLimitBucket.Ask => Ask,
            RateLimitBucket.AskStream => AskStream,
            _ => throw new ArgumentOutOfRangeException(nameof(bucket), bucket, null)
        };
    }
}

public sealed class RateLimitBucketOptions
{
    public int Capacity { get; set; }
    public int RefillTokens { get; set; }
    public int RefillPeriodSeconds { get; set; }
}
