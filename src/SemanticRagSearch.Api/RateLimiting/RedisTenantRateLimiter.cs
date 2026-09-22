using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace SemanticRagSearch.Api.RateLimiting;

public sealed class RedisTenantRateLimiter : ITenantRateLimiter
{
    private const string RedisScript = """
        local capacity = tonumber(ARGV[1])
        local refill_tokens = tonumber(ARGV[2])
        local refill_period_ms = tonumber(ARGV[3])
        local state_ttl_seconds = tonumber(ARGV[4])

        local server_time = redis.call('TIME')
        local now_ms = (tonumber(server_time[1]) * 1000) + math.floor(tonumber(server_time[2]) / 1000)
        local state = redis.call('HMGET', KEYS[1], 'tokens', 'timestamp_ms')
        local tokens = tonumber(state[1]) or capacity
        local previous_ms = tonumber(state[2]) or now_ms
        local elapsed_ms = math.max(0, now_ms - previous_ms)

        tokens = math.min(
            capacity,
            tokens + ((elapsed_ms * refill_tokens) / refill_period_ms))

        local allowed = 0
        local retry_after_seconds = 0
        if tokens >= 1 then
            tokens = tokens - 1
            allowed = 1
        else
            local missing_tokens = 1 - tokens
            retry_after_seconds = math.max(
                1,
                math.ceil((missing_tokens * refill_period_ms) / refill_tokens / 1000))
        end

        redis.call(
            'HSET',
            KEYS[1],
            'tokens', tokens,
            'timestamp_ms', now_ms)
        redis.call('EXPIRE', KEYS[1], state_ttl_seconds)

        return { allowed, retry_after_seconds }
        """;

    private readonly IRedisConnectionProvider _connectionProvider;
    private readonly RateLimitingOptions _options;

    internal RedisTenantRateLimiter(
        IRedisConnectionProvider connectionProvider,
        IOptions<RateLimitingOptions> options)
    {
        _connectionProvider = connectionProvider
            ?? throw new ArgumentNullException(nameof(connectionProvider));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    }

    public async ValueTask<RateLimitDecision> TryAcquireAsync(
        Guid tenantId,
        RateLimitBucket bucket,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RateLimitBucketOptions policy = _options.GetBucket(bucket);

        try
        {
            IConnectionMultiplexer connection =
                await _connectionProvider.GetConnectionAsync(cancellationToken);
            IDatabase database = connection.GetDatabase();
            RedisKey key = CreateRedisKey(tenantId, bucket);
            RedisValue[] arguments =
            [
                policy.Capacity,
                policy.RefillTokens,
                checked(policy.RefillPeriodSeconds * 1_000),
                _options.StateTtlSeconds
            ];

            cancellationToken.ThrowIfCancellationRequested();
            Task<RedisResult> evaluationTask = database.ScriptEvaluateAsync(
                RedisScript,
                [key],
                arguments);
            RedisResult result = await evaluationTask.WaitAsync(cancellationToken);
            return ParseDecision(result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (RateLimitBackendUnavailableException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new RateLimitBackendUnavailableException(
                "The Redis rate-limit backend is unavailable.",
                exception);
        }
    }

    private static RedisKey CreateRedisKey(Guid tenantId, RateLimitBucket bucket)
    {
        string bucketName = bucket switch
        {
            RateLimitBucket.Documents => "documents",
            RateLimitBucket.Search => "search",
            RateLimitBucket.Ask => "ask",
            RateLimitBucket.AskStream => "ask-stream",
            _ => throw new ArgumentOutOfRangeException(nameof(bucket), bucket, null)
        };

        return $"semantic-rag:rate-limit:{{{tenantId:D}}}:{bucketName}";
    }

    private static RateLimitDecision ParseDecision(RedisResult result)
    {
        RedisResult[]? values = (RedisResult[]?)result;
        if (values is null || values.Length != 2)
        {
            throw new InvalidOperationException(
                "Redis returned an invalid rate-limit decision.");
        }

        long allowed = (long)values[0];
        long retryAfterSeconds = (long)values[1];
        if (allowed == 1)
            return RateLimitDecision.Allowed;

        if (allowed != 0 || retryAfterSeconds is < 1 or > int.MaxValue)
        {
            throw new InvalidOperationException(
                "Redis returned an invalid rate-limit decision.");
        }

        return RateLimitDecision.Denied((int)retryAfterSeconds);
    }
}
