using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using SemanticRagSearch.Api.Embedding;
using SemanticRagSearch.Api.Generation;
using SemanticRagSearch.Api.Rag;
using SemanticRagSearch.Api.Search;

namespace SemanticRagSearch.Api.Observability;

public static class ObservabilityServiceCollectionExtensions
{
    private const string DefaultServiceName = "SemanticRagSearch.Api";
    private const string DefaultOtlpEndpoint = "http://localhost:4318";

    public static IServiceCollection AddRagObservability(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddScoped<IEmbeddingService>(serviceProvider =>
            new ObservableEmbeddingService(
                serviceProvider.GetRequiredService<OllamaEmbeddingService>()));
        services.AddScoped<ITextGenerationService>(serviceProvider =>
            new ObservableTextGenerationService(
                serviceProvider.GetRequiredService<OllamaTextGenerationService>()));
        services.AddScoped<ISemanticSearchService>(serviceProvider =>
            new ObservableSemanticSearchService(
                serviceProvider.GetRequiredService<SemanticSearchService>()));
        services.AddScoped<IRagService>(serviceProvider =>
            new ObservableRagService(
                serviceProvider.GetRequiredService<RagService>()));

        string? serviceName = configuration["OpenTelemetry:ServiceName"];
        if (string.IsNullOrWhiteSpace(serviceName))
            serviceName = DefaultServiceName;

        var openTelemetry = services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(serviceName))
            .WithTracing(tracing => tracing
                .AddSource(RagTelemetry.InstrumentationName)
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation())
            .WithMetrics(metrics => metrics
                .AddMeter(RagTelemetry.InstrumentationName)
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation());

        bool exportEnabled =
            configuration.GetValue<bool?>("OpenTelemetry:ExportEnabled") ?? true;
        if (exportEnabled)
        {
            string endpointValue =
                configuration["OpenTelemetry:OtlpEndpoint"] ?? DefaultOtlpEndpoint;
            if (!Uri.TryCreate(endpointValue, UriKind.Absolute, out Uri? endpoint))
            {
                throw new InvalidOperationException(
                    "OpenTelemetry:OtlpEndpoint must be an absolute URI.");
            }

            openTelemetry.UseOtlpExporter(OtlpExportProtocol.HttpProtobuf, endpoint);
        }

        return services;
    }
}
