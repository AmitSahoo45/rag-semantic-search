using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using SemanticRagSearch.Api.Authentication;
using SemanticRagSearch.Api.Domain;
using SemanticRagSearch.Api.Rag;

namespace SemanticRagSearch.IntegrationTests;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Postgres")]
public sealed class PostgresApiKeyTests(PostgresFixture database)
{
    [PostgresFact]
    public async Task RotationRevocationAndDeactivation_TakeEffectOnTheNextRequest()
    {
        string firstKey = ApiKeyCredentials.Generate();
        Guid tenantId = Guid.NewGuid();
        await using var context = database.CreateDbContext();
        var tenant = new Tenant
        {
            TenantId = tenantId,
            TenantName = "API key lifecycle",
            TenantDescription = "Isolated authentication verification",
            Email = $"auth-{tenantId:N}@example.test",
            ApiKeyHash = ApiKeyCredentials.Hash(firstKey)
        };
        context.Tenants.Add(tenant);
        await context.SaveChangesAsync();
        using var factory = new AuthDatabaseFactory(database.ConnectionString);
        using HttpClient client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, await AskAsync(client, tenantId, firstKey));
        Assert.Equal(HttpStatusCode.Forbidden, await AskAsync(client, Guid.NewGuid(), firstKey));
        string rotatedKey = ApiKeyCredentials.Generate();
        tenant.ApiKeyHash = ApiKeyCredentials.Hash(rotatedKey);
        await context.SaveChangesAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, await AskAsync(client, tenantId, firstKey));
        Assert.Equal(HttpStatusCode.OK, await AskAsync(client, tenantId, rotatedKey));

        tenant.IsActive = false;
        await context.SaveChangesAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, await AskAsync(client, tenantId, rotatedKey));
        tenant.IsActive = true;
        tenant.ApiKeyHash = null;
        tenant.ApiKey = rotatedKey; // The legacy plaintext column must never authenticate a request.
        await context.SaveChangesAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, await AskAsync(client, tenantId, rotatedKey));
    }

    [PostgresFact]
    public async Task ProvisionAndRevokeCommands_StoreOnlyHashes()
    {
        Guid tenantId = Guid.NewGuid();
        await using var context = database.CreateDbContext();
        context.Tenants.Add(new Tenant
        {
            TenantId = tenantId,
            TenantName = "Provisioning",
            TenantDescription = "Isolated provisioning verification",
            Email = $"provision-{tenantId:N}@example.test",
            ApiKey = "legacy-plaintext-value"
        });
        await context.SaveChangesAsync();
        using var factory = new AuthDatabaseFactory(database.ConnectionString);
        // Capture the one-time secret rather than writing it to the test output.
        TextWriter original = Console.Out;
        using var captured = new StringWriter();
        string key;
        try
        {
            Console.SetOut(captured);
            Assert.True(await ApiKeyProvisioningCommand.TryRunAsync(["--provision-tenant-key", tenantId.ToString()], factory.Services));
            key = captured.ToString().Trim();
        }
        finally { Console.SetOut(original); }

        Assert.True(ApiKeyCredentials.IsValidFormat(key));
        context.ChangeTracker.Clear();
        Tenant stored = await context.Tenants.SingleAsync(row => row.TenantId == tenantId);
        Assert.Null(stored.ApiKey);
        Assert.Equal(ApiKeyCredentials.Hash(key), stored.ApiKeyHash);
        Assert.NotEqual(key, stored.ApiKeyHash);
        Assert.True(await ApiKeyProvisioningCommand.TryRunAsync(["--revoke-tenant-key", tenantId.ToString()], factory.Services));
        context.ChangeTracker.Clear();
        Assert.Null((await context.Tenants.SingleAsync(row => row.TenantId == tenantId)).ApiKeyHash);
    }

    private static async Task<HttpStatusCode> AskAsync(HttpClient client, Guid tenantId, string key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/ask")
        {
            Content = JsonContent.Create(new RagRequest(tenantId, "question", 1, 0.3))
        };
        request.Headers.Add(ApiKeyCredentials.HeaderName, key);
        using HttpResponseMessage response = await client.SendAsync(request);
        return response.StatusCode;
    }

    private sealed class AuthDatabaseFactory(string connectionString) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("ConnectionStrings:DefaultConnection", connectionString);
            builder.UseSetting("App:RateLimiting:Enabled", "false");
            builder.UseSetting("OpenTelemetry:ExportEnabled", "false");
            builder.ConfigureLogging(logging => logging.ClearProviders());
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IRagService>();
                services.AddSingleton<IRagService, SuccessfulRagService>();
            });
        }
    }

    private sealed class SuccessfulRagService : IRagService
    {
        public Task<RagResponse> AskAsync(RagRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new RagResponse(request.TenantId, request.Question, 1, 0.3, "answer", [], new(0, 0, null)));

        public IAsyncEnumerable<RagStreamEvent> AskStreamAsync(RagRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
