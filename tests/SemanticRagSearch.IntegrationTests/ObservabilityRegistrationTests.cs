using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SemanticRagSearch.Api.Configuration;
using SemanticRagSearch.Api.Embedding;
using SemanticRagSearch.Api.Observability;

namespace SemanticRagSearch.IntegrationTests;

public sealed class ObservabilityRegistrationTests
{
    [Fact]
    public void AddRagObservability_WithExportDisabled_ResolvesDecoratorOverConcreteService()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OpenTelemetry:ExportEnabled"] = "false",
                ["OpenTelemetry:ServiceName"] = "RegistrationTest"
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpClient("Ollama");
        services.AddSingleton<IOptions<OllamaSettings>>(Options.Create(new OllamaSettings()));
        services.AddScoped<OllamaEmbeddingService>();
        services.AddRagObservability(configuration);

        using ServiceProvider provider = services.BuildServiceProvider(validateScopes: true);
        using IServiceScope scope = provider.CreateScope();

        IEmbeddingService service = scope.ServiceProvider.GetRequiredService<IEmbeddingService>();

        Assert.IsType<ObservableEmbeddingService>(service);
    }
}
