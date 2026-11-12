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
            // Uri.TryCreate accepts scheme-less values such as "localhost:4318" (parsed as
            // scheme "localhost"), which the OTLP exporter then fails on silently per batch.
            if (!Uri.TryCreate(endpointValue, UriKind.Absolute, out Uri? endpoint) ||
                !IsHttpOrHttps(endpoint))
            {
                throw new InvalidOperationException(
                    "OpenTelemetry:OtlpEndpoint must be an absolute http or https URI.");
            }

            // Export only bounded traces and metrics. Application logs can contain
            // tenant identifiers and exception details and must stay with local providers.
            openTelemetry
                .WithTracing(tracing => tracing.AddOtlpExporter("tracing", options =>
                {
                    options.Protocol = OtlpExportProtocol.HttpProtobuf;
                    options.Endpoint = new UriBuilder(endpoint)
                    {
                        Path = $"{endpoint.AbsolutePath.TrimEnd('/')}/v1/traces"
                    }.Uri;
                }))
                .WithMetrics(metrics => metrics.AddOtlpExporter("metrics", options =>
                {
                    options.Protocol = OtlpExportProtocol.HttpProtobuf;
                    options.Endpoint = new UriBuilder(endpoint)
                    {
                        Path = $"{endpoint.AbsolutePath.TrimEnd('/')}/v1/metrics"
                    }.Uri;
                }));
        }

        return services;
    }

    private static bool IsHttpOrHttps(Uri endpoint) =>
        string.Equals(endpoint.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
}
