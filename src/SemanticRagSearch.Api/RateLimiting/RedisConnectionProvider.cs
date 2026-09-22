using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace SemanticRagSearch.Api.RateLimiting;

internal interface IRedisConnectionProvider
{
    ValueTask<IConnectionMultiplexer> GetConnectionAsync(
        CancellationToken cancellationToken);
}

internal sealed class RedisConnectionProvider : IRedisConnectionProvider, IDisposable, IAsyncDisposable
{
    private readonly RateLimitingOptions _options;
    private readonly SemaphoreSlim _connectionLock = new(1, 1);
    private IConnectionMultiplexer? _connection;
    private int _disposed;

    public RedisConnectionProvider(IOptions<RateLimitingOptions> options)
    {
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    }

    public async ValueTask<IConnectionMultiplexer> GetConnectionAsync(
        CancellationToken cancellationToken)
    {
        if (_connection is not null)
            return _connection;

        await _connectionLock.WaitAsync(cancellationToken);
        try
        {
            if (_connection is not null)
                return _connection;

            ConfigurationOptions configuration = ConfigurationOptions.Parse(
                _options.RedisConnectionString);
            configuration.AbortOnConnectFail = false;
            configuration.BacklogPolicy = BacklogPolicy.FailFast;
            configuration.ConnectRetry = 0;
            configuration.ConnectTimeout = _options.RedisConnectTimeoutMilliseconds;
            configuration.SyncTimeout = _options.RedisConnectTimeoutMilliseconds;
            configuration.AsyncTimeout = _options.RedisConnectTimeoutMilliseconds;

            Task<ConnectionMultiplexer> connectTask =
                ConnectionMultiplexer.ConnectAsync(configuration);
            try
            {
                _connection = await connectTask.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _ = connectTask.ContinueWith(
                    static completedTask => completedTask.Result.Dispose(),
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnRanToCompletion
                        | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                throw;
            }

            return _connection;
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        IConnectionMultiplexer? connection = Interlocked.Exchange(ref _connection, null);

        try
        {
            if (connection is not null)
            {
                try
                {
                    await connection.CloseAsync(allowCommandsToComplete: false);
                }
                finally
                {
                    connection.Dispose();
                }
            }
        }
        finally
        {
            _connectionLock.Dispose();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        IConnectionMultiplexer? connection = Interlocked.Exchange(ref _connection, null);
        try
        {
            connection?.Dispose();
        }
        finally
        {
            _connectionLock.Dispose();
        }
    }
}
