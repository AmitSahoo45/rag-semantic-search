using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace SemanticRagSearch.IntegrationTests;

public class HealthCheckApiTests
{
    [Fact]
    public async Task Health_ProbesConnectionStringAppliedAfterServiceRegistration()
    {
        using var factory = new UnreachableDatabaseApiFactory();
        using HttpClient client = factory.CreateClient();
        using HttpResponseMessage response = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    private sealed class UnreachableDatabaseApiFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("App:RateLimiting:Enabled", "false");
            builder.UseSetting("OpenTelemetry:ExportEnabled", "false");
            builder.ConfigureLogging(logging => logging.ClearProviders());
            // ConfigureAppConfiguration is applied when the host is built, after Program.cs has registered its
            // services, so only registrations that resolve the connection string lazily honor this override.
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = "Host=127.0.0.1;Port=1;Username=x;Password=x;Database=x;Timeout=1"
            }));
        }
    }
}
