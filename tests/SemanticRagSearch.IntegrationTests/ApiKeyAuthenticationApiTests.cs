using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using SemanticRagSearch.Api.Authentication;
using SemanticRagSearch.Api.Rag;

namespace SemanticRagSearch.IntegrationTests;

public class ApiKeyAuthenticationApiTests
{
    [Theory]
    [InlineData("/api/v1/documents")]
    [InlineData("/api/v1/search")]
    [InlineData("/api/v1/ask")]
    [InlineData("/api/v1/ask/stream")]
    public async Task MissingKey_ReturnsJsonUnauthorizedBeforeAnyServiceWork(string endpoint)
    {
        using var factory = new AuthApiFactory();
        using HttpClient client = factory.CreateClient();
        using HttpResponseMessage response = await client.PostAsJsonAsync(endpoint, Body(TenantApiTestExtensions.ContractTenantId));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains(response.Headers.WwwAuthenticate, challenge => challenge.Scheme == "ApiKey");
    }

    [Theory]
    [InlineData("too-short")]
    [InlineData("test_00000000000000000000000000000000")]
    [InlineData("test_11111111111111111111111111111111,test_11111111111111111111111111111111")]
    public async Task InvalidUnknownOrCombinedKeys_ReturnUnauthorized(string key)
    {
        using var factory = new AuthApiFactory();
        using HttpClient client = factory.CreateClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation(ApiKeyCredentials.HeaderName, key);
        using HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/ask", Body(TenantApiTestExtensions.ContractTenantId));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.DoesNotContain(key, await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("/api/v1/documents")]
    [InlineData("/api/v1/search")]
    [InlineData("/api/v1/ask")]
    [InlineData("/api/v1/ask/stream")]
    public async Task AuthenticatedKey_CannotSelectAnotherTenant(string endpoint)
    {
        using var factory = new AuthApiFactory();
        using HttpClient client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(ApiKeyCredentials.HeaderName, TenantApiTestExtensions.KeyFor(TenantApiTestExtensions.ContractTenantId));
        using HttpResponseMessage response = await client.PostAsJsonAsync(endpoint, Body(Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task MatchingKey_CanCallRagEndpoint()
    {
        using var factory = new AuthApiFactory();
        using HttpClient client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(ApiKeyCredentials.HeaderName, TenantApiTestExtensions.KeyFor(TenantApiTestExtensions.ContractTenantId));
        using HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/ask", Body(TenantApiTestExtensions.ContractTenantId));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task RootEndpoint_RemainsAnonymous()
    {
        using var factory = new AuthApiFactory();
        using HttpClient client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/")).StatusCode);
    }

    [Fact]
    public async Task AnonymousEndpoint_WithApiKey_DoesNotQueryTenantStore()
    {
        using var factory = new AuthApiFactory(new ThrowingTenantStore());
        using HttpClient client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(ApiKeyCredentials.HeaderName, TenantApiTestExtensions.KeyFor(TenantApiTestExtensions.ContractTenantId));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/")).StatusCode);
    }

    private static object Body(Guid tenantId) => new { tenantId, title = "Title", content = "Content", query = "Question", question = "Question" };

    private sealed class AuthApiFactory(IApiKeyTenantStore? tenantStore = null) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("App:RateLimiting:Enabled", "false");
            builder.UseSetting("OpenTelemetry:ExportEnabled", "false");
            builder.ConfigureLogging(logging => logging.ClearProviders());
            builder.ConfigureTestServices(services =>
            {
                services.AddContractTenantStore();
                if (tenantStore is not null)
                {
                    services.RemoveAll<IApiKeyTenantStore>();
                    services.AddSingleton<IApiKeyTenantStore>(tenantStore);
                }
                services.RemoveAll<IRagService>();
                services.AddSingleton<IRagService, SuccessfulRagService>();
            });
        }
    }

    private sealed class ThrowingTenantStore : IApiKeyTenantStore
    {
        public Task<Guid?> FindActiveTenantAsync(string keyHash, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The tenant store must not be queried for anonymous endpoints.");
    }

    private sealed class SuccessfulRagService : IRagService
    {
        public Task<RagResponse> AskAsync(RagRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new RagResponse(request.TenantId, request.Question, 1, 0.3, "answer", [], new(0, 0, null)));

        public async IAsyncEnumerable<RagStreamEvent> AskStreamAsync(RagRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield return new RagStreamEvent("answer", false, null, null);
            yield return new RagStreamEvent(null, true, [], new(0, 0, 0));
        }
    }
}
