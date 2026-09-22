using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SemanticRagSearch.Api.Authentication;

namespace SemanticRagSearch.IntegrationTests;

internal static class TenantApiTestExtensions
{
    internal static readonly Guid ContractTenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    internal static string KeyFor(Guid tenantId) => $"test_{tenantId:N}";
    internal static string HashFor(Guid tenantId) => ApiKeyCredentials.Hash(KeyFor(tenantId));

    internal static void AddContractTenantStore(this IServiceCollection services, Guid? tenantId = null)
    {
        services.RemoveAll<IApiKeyTenantStore>();
        services.AddSingleton<IApiKeyTenantStore>(new ContractTenantStore(tenantId ?? ContractTenantId));
    }

    internal static async Task<HttpResponseMessage> PostTenantJsonAsync<T>(this HttpClient client,
        string uri, T body, CancellationToken cancellationToken = default) where T : ITenantRequest
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = JsonContent.Create(body) };
        request.Headers.Add(ApiKeyCredentials.HeaderName, KeyFor(body.TenantId));
        return await client.SendAsync(request, cancellationToken);
    }

    private sealed class ContractTenantStore(Guid tenantId) : IApiKeyTenantStore
    {
        public Task<Guid?> FindActiveTenantAsync(string keyHash, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<Guid?>(keyHash == HashFor(tenantId) ? tenantId : null);
        }
    }
}
