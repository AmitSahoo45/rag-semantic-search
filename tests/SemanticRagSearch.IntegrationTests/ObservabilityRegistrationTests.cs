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

    [Theory]
    [InlineData("localhost:4318")]
    [InlineData("ftp://localhost:4318")]
    [InlineData("not an absolute uri")]
    public void AddRagObservability_WithNonHttpOtlpEndpoint_ThrowsInvalidOperation(string endpoint)
    {
        IConfiguration configuration = BuildExportConfiguration(endpoint);
        var services = new ServiceCollection();

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
        {
            services.AddRagObservability(configuration);
        });

        Assert.Contains("OpenTelemetry:OtlpEndpoint", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://localhost:4318")]
    [InlineData("HTTPS://collector.invalid:4318/otel")]
    public void AddRagObservability_WithHttpOtlpEndpoint_AcceptsSchemeCaseInsensitively(string endpoint)
    {
        IConfiguration configuration = BuildExportConfiguration(endpoint);
        var services = new ServiceCollection();

        Exception? exception = Record.Exception(() =>
        {
            services.AddRagObservability(configuration);
        });

        Assert.Null(exception);
    }

    private static IConfiguration BuildExportConfiguration(string endpoint) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OpenTelemetry:ExportEnabled"] = "true",
                ["OpenTelemetry:OtlpEndpoint"] = endpoint,
                ["OpenTelemetry:ServiceName"] = "RegistrationTest"
            })
            .Build();
}
