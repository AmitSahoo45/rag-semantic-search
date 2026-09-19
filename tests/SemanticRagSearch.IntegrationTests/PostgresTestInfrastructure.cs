using Microsoft.EntityFrameworkCore;
using Pgvector.EntityFrameworkCore;
using SemanticRagSearch.Api.Data;
using Testcontainers.PostgreSql;

namespace SemanticRagSearch.IntegrationTests;

public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (!PostgresFixture.IsPostgresEnabled)
        {
            Skip = "Set RAG_RUN_POSTGRES_TESTS=1 to run PostgreSQL integration tests.";
        }
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "Postgres";
}

public sealed class PostgresFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _container;

    public static bool IsPostgresEnabled => IsEnabled("RAG_RUN_POSTGRES_TESTS");

    public static bool IsLiveEnabled => IsEnabled("RAG_RUN_LIVE_TESTS");

    public string ConnectionString => _container?.GetConnectionString()
        ?? throw new InvalidOperationException("The PostgreSQL test container has not been initialized.");

    public async Task InitializeAsync()
    {
        if (!IsPostgresEnabled && !IsLiveEnabled)
            return;

        _container = new PostgreSqlBuilder("pgvector/pgvector:pg16")
            .WithDatabase("semantic_rag_tests")
            .WithUsername("postgres")
            .WithPassword("postgres")
            .Build();

        try
        {
            await _container.StartAsync();

            await using AppDbContext dbContext = CreateDbContext();
            await dbContext.Database.MigrateAsync();
        }
        catch
        {
            await _container.DisposeAsync();
            _container = null;
            throw;
        }
    }

    public AppDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(
                ConnectionString,
                npgsqlOptions => npgsqlOptions.UseVector())
            .Options;

        return new AppDbContext(options);
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
            await _container.DisposeAsync();
    }

    private static bool IsEnabled(string variableName)
    {
        return string.Equals(
            Environment.GetEnvironmentVariable(variableName),
            "1",
            StringComparison.Ordinal);
    }
}
