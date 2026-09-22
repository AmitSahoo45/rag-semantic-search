using Microsoft.EntityFrameworkCore;
using SemanticRagSearch.Api.Data;

namespace SemanticRagSearch.Api.Authentication;

public interface IApiKeyTenantStore
{
    Task<Guid?> FindActiveTenantAsync(string keyHash, CancellationToken cancellationToken);
}

internal sealed class ApiKeyTenantStore(AppDbContext dbContext) : IApiKeyTenantStore
{
    public Task<Guid?> FindActiveTenantAsync(string keyHash, CancellationToken cancellationToken) =>
        dbContext.Tenants.AsNoTracking()
            .Where(tenant => tenant.IsActive && tenant.ApiKeyHash == keyHash)
            .Select(tenant => (Guid?)tenant.TenantId)
            .SingleOrDefaultAsync(cancellationToken);
}
