using Microsoft.EntityFrameworkCore;
using SemanticRagSearch.Api.Data;

namespace SemanticRagSearch.Api.Authentication;

public static class ApiKeyProvisioningCommand
{
    public static async Task<bool> TryRunAsync(string[] args, IServiceProvider services)
    {
        int provision = Array.IndexOf(args, "--provision-tenant-key");
        int revoke = Array.IndexOf(args, "--revoke-tenant-key");
        if (provision < 0 && revoke < 0)
            return false;

        int index = Math.Max(provision, revoke);
        if (args.Count(argument => argument is "--provision-tenant-key" or "--revoke-tenant-key") != 1 || index + 1 >= args.Length ||
            !Guid.TryParse(args[index + 1], out Guid tenantId) || tenantId == Guid.Empty)
        {
            Console.Error.WriteLine("Supply exactly one --provision-tenant-key <tenant-id> or --revoke-tenant-key <tenant-id> command.");
            Environment.ExitCode = 2;
            return true;
        }

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using AsyncServiceScope scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var tenant = await dbContext.Tenants.SingleOrDefaultAsync(tenant => tenant.TenantId == tenantId, deadline.Token);
        if (tenant is null || (provision >= 0 && !tenant.IsActive))
        {
            Console.Error.WriteLine("Tenant was not found or is inactive.");
            Environment.ExitCode = 2;
            return true;
        }

        string? key = provision >= 0 ? ApiKeyCredentials.Generate() : null;
        tenant.ApiKeyHash = key is null ? null : ApiKeyCredentials.Hash(key);
        tenant.ApiKey = null;
        tenant.LastModifiedDate = DateTime.UtcNow;
        await dbContext.SaveChangesAsync(deadline.Token);
        Console.WriteLine(key is null ? "API key revoked." : key);
        return true;
    }
}
