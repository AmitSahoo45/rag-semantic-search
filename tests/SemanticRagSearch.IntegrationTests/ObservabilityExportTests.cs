using System.Collections.Concurrent;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using SemanticRagSearch.Api.Configuration;
using SemanticRagSearch.Api.Errors;
using SemanticRagSearch.Api.Generation;
using SemanticRagSearch.Api.Observability;
using SemanticRagSearch.Api.Ollama;
using SemanticRagSearch.Api.Rag;
using SemanticRagSearch.Api.Search;

namespace SemanticRagSearch.IntegrationTests;

public sealed class ObservabilityExportTests
{
    private static readonly Guid TenantId = Guid.Parse("72626e29-ffec-4d30-976e-f9ca9deffb35");
    private static readonly Guid DocumentId = Guid.Parse("63efc813-a83a-40a0-9794-ac2a484ecf28");
    private const string PrivateQuestion = "PRIVATE-QUESTION-69d3";
    private const string PrivateDocument = "PRIVATE-DOCUMENT-69d3";
    private const string PrivateAnswer = "PRIVATE-ANSWER-69d3";
    private const string PrivateException = "PRIVATE-EXCEPTION-69d3";
    private const string PrivateProviderException = "PRIVATE-PROVIDER-69d3\ncredential=秘密";

    [Theory]
    [InlineData(null, "http://localhost:4318/v1/traces", "http://localhost:4318/v1/metrics")]
    [InlineData("http://collector.invalid:4318", "http://collector.invalid:4318/v1/traces", "http://collector.invalid:4318/v1/metrics")]
    [InlineData("http://collector.invalid:4318/", "http://collector.invalid:4318/v1/traces", "http://collector.invalid:4318/v1/metrics")]
    [InlineData("http://collector.invalid:4318/otel", "http://collector.invalid:4318/otel/v1/traces", "http://collector.invalid:4318/otel/v1/metrics")]
    [InlineData("http://collector.invalid:4318/otel/", "http://collector.invalid:4318/otel/v1/traces", "http://collector.invalid:4318/otel/v1/metrics")]
    public async Task AddRagObservability_ExportsTracesAndMetricsWithoutExportingPrivateLogs(
        string? endpoint,
        string traceEndpoint,
        string metricEndpoint)
    {
        ExportCapture capture = await ExerciseTelemetryAsync(endpoint);

        // Inspect the real serialized OTLP bodies, rather than replacing the exporters.
        Assert.Contains(capture.Exports, export =>
            export.Url == traceEndpoint && export.Body.Contains("rag.ask", StringComparison.Ordinal));
        Assert.Contains(capture.Exports, export =>
            export.Url == metricEndpoint && export.Body.Contains("rag.operation.calls", StringComparison.Ordinal));
        Assert.All(capture.Exports, export =>
        {
            Assert.Contains("ExportPrivacyTest", export.Body);
            Assert.DoesNotContain(TenantId.ToString(), export.Body);
            Assert.DoesNotContain(DocumentId.ToString(), export.Body);
            Assert.DoesNotContain(PrivateQuestion, export.Body);
            Assert.DoesNotContain(PrivateDocument, export.Body);
            Assert.DoesNotContain(PrivateAnswer, export.Body);
            Assert.DoesNotContain(PrivateException, export.Body);
            Assert.DoesNotContain(PrivateProviderException, export.Body);
            Assert.True(export.Url == traceEndpoint || export.Url == metricEndpoint,
                $"Unexpected telemetry destination: {export.Url}");
        });
        AssertLocalLoggingPreserved(capture);
    }

    [Fact]
    public async Task AddRagObservability_WithExportDisabled_KeepsLocalLogsWithoutSendingTelemetry()
    {
        ExportCapture capture = await ExerciseTelemetryAsync(exportEnabled: false);

        Assert.Empty(capture.Exports);
        AssertLocalLoggingPreserved(capture);
    }

    private static void AssertLocalLoggingPreserved(ExportCapture capture)
    {
        Assert.Contains(capture.Logs, entry => entry.Message.Contains(TenantId.ToString(), StringComparison.Ordinal));
        Assert.Contains(capture.Logs, entry => entry.Exception?.Message == PrivateException);
        Assert.Contains(capture.Logs, entry => entry.Exception?.Message == PrivateProviderException);
    }

    private static async Task<ExportCapture> ExerciseTelemetryAsync(
        string? endpoint = null,
        bool? exportEnabled = null)
    {
        var exports = new ConcurrentQueue<ExportRequest>();
        var localLogs = new LocalLogProvider();
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OpenTelemetry:ExportEnabled"] = exportEnabled?.ToString(),
                ["OpenTelemetry:OtlpEndpoint"] = endpoint,
                ["OpenTelemetry:ServiceName"] = "ExportPrivacyTest"
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.AddProvider(localLogs));
        services.AddRagObservability(configuration);
        services.ConfigureAll<OtlpExporterOptions>(options =>
            options.HttpClientFactory = () => new HttpClient(new CaptureHandler(exports)));

        using (ServiceProvider provider = services.BuildServiceProvider())
        {
            TracerProvider traces = provider.GetRequiredService<TracerProvider>();
            MeterProvider metrics = provider.GetRequiredService<MeterProvider>();
            ILoggerFactory logging = provider.GetRequiredService<ILoggerFactory>();
            IOptions<RagSettings> settings = Options.Create(new RagSettings());
            var tokenEstimator = new ApproximateTextTokenEstimator(settings);
            var generation = new GenerationStub();
            var rag = new ObservableRagService(new RagService(
                new SearchStub(),
                generation,
                new ContextAssembler(tokenEstimator, logging.CreateLogger<ContextAssembler>()),
                tokenEstimator,
                settings,
                logging.CreateLogger<RagService>()));
            var request = new RagRequest(TenantId, PrivateQuestion, 1, 0.1);

            RagResponse answer = await rag.AskAsync(request);
            Assert.Equal(PrivateAnswer, answer.Answer);
            await foreach (RagStreamEvent _ in rag.AskStreamAsync(request))
            {
            }

            generation.Failure = new OllamaProviderException(
                OllamaFailureKind.Unavailable, PrivateProviderException);
            OllamaProviderException failure = await Assert.ThrowsAsync<OllamaProviderException>(
                () => rag.AskAsync(request));
            var handler = new GlobalExceptionHandler(logging.CreateLogger<GlobalExceptionHandler>());
            foreach (Exception exception in new Exception[] { failure, new InvalidOperationException(PrivateException) })
            {
                using var responseBody = new MemoryStream();
                var context = new DefaultHttpContext();
                context.Response.Body = responseBody;
                Assert.True(await handler.TryHandleAsync(context, exception, CancellationToken.None));
            }

            Assert.True(traces.ForceFlush(5000));
            Assert.True(metrics.ForceFlush(5000));
            // Flush an accidentally registered log exporter too, so batching cannot hide a leak.
            if (provider.GetService<LoggerProvider>() is { } logs)
                Assert.True(logs.ForceFlush(5000));
        }

        return new ExportCapture(exports.ToArray(), localLogs.Entries.ToArray());
    }

    private sealed record ExportRequest(string Url, string Body);
    private sealed record LogEntry(string Message, Exception? Exception);
    private sealed record ExportCapture(ExportRequest[] Exports, LogEntry[] Logs);

    private sealed class CaptureHandler(ConcurrentQueue<ExportRequest> exports) : HttpMessageHandler
    {
        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            byte[] payload = request.Content!.ReadAsByteArrayAsync(cancellationToken).GetAwaiter().GetResult();
            exports.Enqueue(new ExportRequest(request.RequestUri!.AbsoluteUri, Encoding.UTF8.GetString(payload)));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(Send(request, cancellationToken));
    }

    private sealed class LocalLogProvider : ILoggerProvider
    {
        public ConcurrentQueue<LogEntry> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) => new LocalLogger(Entries);

        public void Dispose() { }

        private sealed class LocalLogger(ConcurrentQueue<LogEntry> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
                Exception? exception, Func<TState, Exception?, string> formatter)
                => entries.Enqueue(new LogEntry(formatter(state, exception), exception));
        }
    }

    private sealed class SearchStub : ISemanticSearchService
    {
        public Task<SemanticSearchResponse> SearchAsync(SemanticSearchRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new SemanticSearchResponse(request.TenantId, request.Query, 1, 0.1,
                [new SemanticSearchMatch(Guid.NewGuid(), DocumentId, "Test document", null, 0, PrivateDocument, 0.9)]));
    }

    private sealed class GenerationStub : ITextGenerationService
    {
        public Exception? Failure { get; set; }

        public Task<string> GenerateAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken = default)
            => Failure is null ? Task.FromResult(PrivateAnswer) : Task.FromException<string>(Failure);

        public async IAsyncEnumerable<string> GenerateStreamAsync(string systemPrompt, string userPrompt,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return PrivateAnswer;
        }
    }
}
