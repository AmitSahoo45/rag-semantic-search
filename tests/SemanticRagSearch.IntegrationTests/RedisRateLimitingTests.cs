using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using SemanticRagSearch.Api.Rag;
using SemanticRagSearch.Api.RateLimiting;
using StackExchange.Redis;

namespace SemanticRagSearch.IntegrationTests;

public sealed class RedisFactAttribute : FactAttribute
{
    public RedisFactAttribute()
    {
        if (!RedisFixture.IsRedisEnabled)
        {
            Skip = "Set RAG_RUN_REDIS_TESTS=1 to run Redis integration tests.";
        }
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class RedisCollection : ICollectionFixture<RedisFixture>
{
    public const string Name = "Redis";
}

public sealed class RedisFixture : IAsyncLifetime
{
    private const int RedisPort = 6379;
    private IContainer? _container;

    public static bool IsRedisEnabled => string.Equals(
        Environment.GetEnvironmentVariable("RAG_RUN_REDIS_TESTS"),
        "1",
        StringComparison.Ordinal);

    public string ConnectionString => _container is null
        ? throw new InvalidOperationException("The Redis test container has not been initialized.")
        : $"{_container.Hostname}:{_container.GetMappedPublicPort(RedisPort)},password=semantic-rag-tests,abortConnect=false";

    public string InvalidPasswordConnectionString
    {
        get
        {
            ConfigurationOptions configuration = ConfigurationOptions.Parse(ConnectionString);
            configuration.Password = "invalid-test-password";
            return configuration.ToString(includePassword: true);
        }
    }

    public async Task InitializeAsync()
    {
        if (!IsRedisEnabled)
            return;

        _container = new ContainerBuilder("redis:7-alpine")
            .WithCommand("redis-server", "--requirepass", "semantic-rag-tests")
            .WithPortBinding(RedisPort, true)
            .WithWaitStrategy(
                Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(RedisPort))
            .Build();

        try
        {
            await _container.StartAsync();
        }
        catch
        {
            await _container.DisposeAsync();
            _container = null;
            throw;
        }
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
            await _container.DisposeAsync();
    }
}

[Collection(RedisCollection.Name)]
public sealed class RedisRateLimitingTests(RedisFixture fixture)
{
    [RedisFact]
    [Trait("Category", "Redis")]
    public async Task ApiPipeline_AuthenticatesThenLimitsAskAndKeepsStreamBucketSeparate()
    {
        using var factory = new RateLimitedApiFactory(fixture.ConnectionString);
        using HttpClient client = factory.CreateClient();
        var request = new RagRequest(
            factory.TenantId,
            "Question",
            3,
            0.4);

        using HttpResponseMessage unauthenticated = await client.PostAsJsonAsync(
            "/api/v1/ask",
            request);
        using HttpResponseMessage admitted = await client.PostTenantJsonAsync(
            "/api/v1/ask",
            request);
        using HttpResponseMessage exhausted = await client.PostTenantJsonAsync(
            "/api/v1/ask",
            request);
        using HttpResponseMessage separateStreamBucket = await client.PostTenantJsonAsync(
            "/api/v1/ask/stream",
            request);

        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
        Assert.Equal(HttpStatusCode.OK, admitted.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, exhausted.StatusCode);
        Assert.True(
            int.TryParse(
                exhausted.Headers.GetValues("Retry-After").Single(),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int retryAfterSeconds));
        Assert.True(retryAfterSeconds > 0);
        Assert.Equal(HttpStatusCode.OK, separateStreamBucket.StatusCode);
    }

    [RedisFact]
    [Trait("Category", "Redis")]
    public async Task ApiPipeline_ReturnsSanitized503WhenRedisCannotAuthenticate()
    {
        using var factory = new RateLimitedApiFactory(
            fixture.InvalidPasswordConnectionString);
        using HttpClient client = factory.CreateClient();
        var request = new RagRequest(
            factory.TenantId,
            "Question",
            3,
            0.4);

        using HttpResponseMessage response = await client.PostTenantJsonAsync(
            "/api/v1/ask",
            request);
        string body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("Rate limiting is temporarily unavailable.", body);
        Assert.DoesNotContain("invalid-test-password", body, StringComparison.Ordinal);
    }

    [RedisFact]
    [Trait("Category", "Redis")]
    public async Task ConcurrentRequests_AtomicallyAdmitOnlyBucketCapacity()
    {
        await using ServiceProvider provider = CreateProvider(
            fixture.ConnectionString,
            capacity: 10,
            refillTokens: 1,
            refillPeriodSeconds: 3_600,
            stateTtlSeconds: 86_400);
        ITenantRateLimiter limiter = provider.GetRequiredService<ITenantRateLimiter>();
        Guid tenantId = Guid.NewGuid();

        RateLimitDecision[] decisions = await Task.WhenAll(
            Enumerable.Range(0, 50)
                .Select(_ => limiter
                    .TryAcquireAsync(tenantId, RateLimitBucket.Ask)
                    .AsTask()));

        Assert.Equal(10, decisions.Count(decision => decision.IsAllowed));
        Assert.Equal(40, decisions.Count(decision => !decision.IsAllowed));
    }

    [RedisFact]
    [Trait("Category", "Redis")]
    public async Task Buckets_AreIsolatedByTenantAndEndpoint()
    {
        await using ServiceProvider provider = CreateProvider(
            fixture.ConnectionString,
            capacity: 1,
            refillTokens: 1,
            refillPeriodSeconds: 3_600,
            stateTtlSeconds: 7_200);
        ITenantRateLimiter limiter = provider.GetRequiredService<ITenantRateLimiter>();
        Guid tenantA = Guid.NewGuid();
        Guid tenantB = Guid.NewGuid();

        RateLimitDecision tenantADocumentsFirst = await limiter.TryAcquireAsync(
            tenantA,
            RateLimitBucket.Documents);
        RateLimitDecision tenantADocumentsSecond = await limiter.TryAcquireAsync(
            tenantA,
            RateLimitBucket.Documents);
        RateLimitDecision tenantASearch = await limiter.TryAcquireAsync(
            tenantA,
            RateLimitBucket.Search);
        RateLimitDecision tenantBDocuments = await limiter.TryAcquireAsync(
            tenantB,
            RateLimitBucket.Documents);

        Assert.True(tenantADocumentsFirst.IsAllowed);
        Assert.False(tenantADocumentsSecond.IsAllowed);
        Assert.True(tenantASearch.IsAllowed);
        Assert.True(tenantBDocuments.IsAllowed);

        using ConnectionMultiplexer inspectionConnection =
            await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        bool clusterTaggedKeyExists = await inspectionConnection
            .GetDatabase()
            .KeyExistsAsync($"semantic-rag:rate-limit:{{{tenantA:D}}}:documents");
        Assert.True(clusterTaggedKeyExists);
    }

    [RedisFact]
    [Trait("Category", "Redis")]
    public async Task ExhaustedBucket_UsesRedisTimeAndAdmitsAfterRefill()
    {
        await using ServiceProvider provider = CreateProvider(
            fixture.ConnectionString,
            capacity: 1,
            refillTokens: 1,
            refillPeriodSeconds: 1,
            stateTtlSeconds: 60);
        ITenantRateLimiter limiter = provider.GetRequiredService<ITenantRateLimiter>();
        Guid tenantId = Guid.NewGuid();

        RateLimitDecision first = await limiter.TryAcquireAsync(
            tenantId,
            RateLimitBucket.AskStream);
        RateLimitDecision exhausted = await limiter.TryAcquireAsync(
            tenantId,
            RateLimitBucket.AskStream);
        await Task.Delay(TimeSpan.FromMilliseconds(1_100));
        RateLimitDecision refilled = await limiter.TryAcquireAsync(
            tenantId,
            RateLimitBucket.AskStream);

        Assert.True(first.IsAllowed);
        Assert.False(exhausted.IsAllowed);
        Assert.Equal(1, exhausted.RetryAfterSeconds);
        Assert.True(refilled.IsAllowed);
    }

    [RedisFact]
    [Trait("Category", "Redis")]
    public async Task UnavailableRedis_FailsClosedInsteadOfAdmittingRequest()
    {
        await using ServiceProvider provider = CreateProvider(
            fixture.InvalidPasswordConnectionString,
            capacity: 1,
            refillTokens: 1,
            refillPeriodSeconds: 1,
            stateTtlSeconds: 60,
            connectTimeoutMilliseconds: 100);
        ITenantRateLimiter limiter = provider.GetRequiredService<ITenantRateLimiter>();

        await Assert.ThrowsAsync<RateLimitBackendUnavailableException>(
            () => limiter.TryAcquireAsync(
                    Guid.NewGuid(),
                    RateLimitBucket.Search)
                .AsTask());
    }

    private static ServiceProvider CreateProvider(
        string connectionString,
        int capacity,
        int refillTokens,
        int refillPeriodSeconds,
        int stateTtlSeconds,
        int connectTimeoutMilliseconds = 2_000)
    {
        var values = new Dictionary<string, string?>
        {
            ["App:RateLimiting:RedisConnectionString"] = connectionString,
            ["App:RateLimiting:RedisConnectTimeoutMilliseconds"] = connectTimeoutMilliseconds.ToString(),
            ["App:RateLimiting:StateTtlSeconds"] = stateTtlSeconds.ToString()
        };

        foreach (string bucket in new[] { "Documents", "Search", "Ask", "AskStream" })
        {
            values[$"App:RateLimiting:{bucket}:Capacity"] = capacity.ToString();
            values[$"App:RateLimiting:{bucket}:RefillTokens"] = refillTokens.ToString();
            values[$"App:RateLimiting:{bucket}:RefillPeriodSeconds"] = refillPeriodSeconds.ToString();
        }

        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
        var services = new ServiceCollection();
        services.AddTenantRateLimiting(configuration);
        return services.BuildServiceProvider();
    }

    private sealed class RateLimitedApiFactory(string redisConnectionString)
        : WebApplicationFactory<Program>
    {
        public Guid TenantId { get; } = Guid.NewGuid();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("App:RateLimiting:Enabled", "true");
            builder.UseSetting(
                "App:RateLimiting:RedisConnectionString",
                redisConnectionString);
            builder.UseSetting(
                "App:RateLimiting:RedisConnectTimeoutMilliseconds",
                "250");
            builder.UseSetting("App:RateLimiting:StateTtlSeconds", "7200");
            builder.UseSetting("App:RateLimiting:Ask:Capacity", "1");
            builder.UseSetting("App:RateLimiting:Ask:RefillTokens", "1");
            builder.UseSetting("App:RateLimiting:Ask:RefillPeriodSeconds", "3600");
            builder.UseSetting("App:RateLimiting:AskStream:Capacity", "1");
            builder.UseSetting("App:RateLimiting:AskStream:RefillTokens", "1");
            builder.UseSetting("App:RateLimiting:AskStream:RefillPeriodSeconds", "3600");
            builder.UseSetting("OpenTelemetry:ExportEnabled", "false");
            builder.ConfigureLogging(logging => logging.ClearProviders());
            builder.ConfigureTestServices(services =>
            {
                services.AddContractTenantStore(TenantId);
                services.RemoveAll<IRagService>();
                services.AddSingleton<IRagService, SuccessfulRagService>();
            });
        }
    }

    private sealed class SuccessfulRagService : IRagService
    {
        public Task<RagResponse> AskAsync(
            RagRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(
                new RagResponse(
                    request.TenantId,
                    request.Question,
                    request.TopK ?? 5,
                    request.MinSimilarity ?? 0.3,
                    "answer",
                    [],
                    new RagExecutionMetadata(0, 0, null)));
        }

        public async IAsyncEnumerable<RagStreamEvent> AskStreamAsync(
            RagRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield return new RagStreamEvent("answer", false, null, null);
            yield return new RagStreamEvent(
                null,
                true,
                [],
                new RagExecutionMetadata(0, 0, 0));
        }
    }
}
