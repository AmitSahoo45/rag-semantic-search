using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Pgvector;
using SemanticRagSearch.Api.VectorStore;

namespace SemanticRagSearch.UnitTests;

public class PgVectorStoreServiceTests
{
    public static IEnumerable<object?[]> InvalidSearchArguments()
    {
        Guid tenantId = Guid.NewGuid();
        var vector = new Vector(new[] { 1f, 0f });
        yield return [Guid.Empty, vector, 1, 0.0];
        yield return [tenantId, null, 1, 0.0];
        yield return [tenantId, new Vector(Array.Empty<float>()), 1, 0.0];
        yield return [tenantId, vector, 0, 0.0];
        yield return [tenantId, vector, -1, 0.0];
        yield return [tenantId, vector, 1, -0.1];
        yield return [tenantId, vector, 1, 1.1];
    }

    [Theory]
    [MemberData(nameof(InvalidSearchArguments))]
    public async Task SearchSimilarAsync_InvalidInputFailsBeforeOpeningConnection(
        Guid tenantId, Vector? embedding, int topK, double minSimilarity)
    {
        // No database is required: every case must fail before any connection attempt.
        await using NpgsqlDataSource dataSource = NpgsqlDataSource.Create(
            "Host=127.0.0.1;Port=1;Database=unused;Username=unused;Timeout=1");
        var service = new PgVectorStoreService(dataSource, NullLogger<PgVectorStoreService>.Instance);

        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            service.SearchSimilarAsync(tenantId, embedding!, topK, minSimilarity));
    }
}
