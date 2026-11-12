using System.Net;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using SemanticRagSearch.Api.Generation;
using SemanticRagSearch.Api.Ollama;
using SemanticRagSearch.Api.Rag;
using SemanticRagSearch.Api.Search;

namespace SemanticRagSearch.IntegrationTests;

public class RagStreamingApiTests
{
    private static readonly Guid TenantId =
        Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Theory]
    [InlineData(
        "empty-tenant",
        "TenantId cannot be empty. (Parameter 'request')")]
    [InlineData(
        "blank-question",
        "Question cannot be empty. (Parameter 'request')")]
    [InlineData(
        "long-question",
        "Question cannot exceed 2000 characters. (Parameter 'request')")]
    public async Task InvalidRequest_FromRealRagService_ReturnsJsonBadRequest(
        string invalidCase,
        string expectedError)
    {
        RagRequest request = invalidCase switch
        {
            "empty-tenant" => new RagRequest(Guid.Empty, "question", 3, 0.4),
            "long-question" => new RagRequest(
                TenantId,
                new string('q', 2_001),
                3,
                0.4),
            _ => new RagRequest(TenantId, " ", 3, 0.4)
        };
        var search = new ControlledSearchService(
            (_, _) => throw new InvalidOperationException("Search must not run."));
        using var factory = new StreamingApiFactory(
            search,
            new ControlledGenerationService(EmptyStream));
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await PostStreamAsync(
            client,
            request);
        string body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.StartsWith("application/json", response.Content.Headers.ContentType?.ToString());
        Assert.Equal(expectedError, ReadError(body));
        Assert.Equal(0, search.CallCount);
    }

    [Fact]
    public async Task UnbindableRequestBody_ReturnsJsonProblemDetailsBadRequest()
    {
        var search = new ControlledSearchService(
            (_, _) => throw new InvalidOperationException("Search must not run."));
        using var factory = new StreamingApiFactory(
            search,
            new ControlledGenerationService(EmptyStream));
        using HttpClient client = factory.CreateClient();
        using var message = new HttpRequestMessage(HttpMethod.Post, "/api/v1/ask/stream")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(
                    new { tenantId = TenantId, question = "q", topK = "five" }),
                Encoding.UTF8,
                "application/json")
        };

        using HttpResponseMessage response = await client.SendAsync(
            message,
            HttpCompletionOption.ResponseHeadersRead);
        string body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.StartsWith(
            "application/problem+json",
            response.Content.Headers.ContentType?.ToString());
        Assert.NotEmpty(body);
        using JsonDocument problem = JsonDocument.Parse(body);
        Assert.Equal(400, problem.RootElement.GetProperty("status").GetInt32());
        Assert.NotEmpty(problem.RootElement.GetProperty("errors").EnumerateObject());
        Assert.Contains("topK", body);
        Assert.Equal(0, search.CallCount);
    }

    [Fact]
    public async Task MissingTenant_FromRealRagService_ReturnsJsonNotFound()
    {
        var search = new ControlledSearchService(
            (_, _) => throw new KeyNotFoundException("tenant missing"));
        using var factory = new StreamingApiFactory(
            search,
            new ControlledGenerationService(EmptyStream));
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await PostStreamAsync(
            client,
            new RagRequest(TenantId, "question", 3, 0.4));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.StartsWith("application/json", response.Content.Headers.ContentType?.ToString());
        Assert.Equal(
            "tenant missing",
            ReadError(await response.Content.ReadAsStringAsync()));
    }

    [Theory]
    [InlineData(
        OllamaFailureKind.InvalidResponse,
        HttpStatusCode.BadGateway,
        "The AI provider returned an invalid response.")]
    [InlineData(
        OllamaFailureKind.Unavailable,
        HttpStatusCode.ServiceUnavailable,
        "The AI provider is unavailable.")]
    [InlineData(
        OllamaFailureKind.Timeout,
        HttpStatusCode.GatewayTimeout,
        "The AI provider timed out.")]
    public async Task ProviderFailureBeforeFirstEvent_ReturnsSanitizedJsonError(
        OllamaFailureKind failureKind,
        HttpStatusCode expectedStatus,
        string expectedError)
    {
        var search = new ControlledSearchService(
            (_, _) => throw new OllamaProviderException(
                failureKind,
                "secret provider detail"));
        using var factory = new StreamingApiFactory(
            search,
            new ControlledGenerationService(EmptyStream));
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await PostStreamAsync(
            client,
            new RagRequest(TenantId, "question", 3, 0.4));
        string body = await response.Content.ReadAsStringAsync();

        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.StartsWith("application/json", response.Content.Headers.ContentType?.ToString());
        Assert.Equal(expectedError, ReadError(body));
        Assert.DoesNotContain("secret provider detail", body);
    }

    [Fact]
    public async Task UnexpectedFailureBeforeFirstEvent_ReturnsSanitizedJsonError()
    {
        var search = new ControlledSearchService(
            (_, _) => throw new InvalidOperationException("secret internal detail"));
        using var factory = new StreamingApiFactory(
            search,
            new ControlledGenerationService(EmptyStream));
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await PostStreamAsync(
            client,
            new RagRequest(TenantId, "question", 3, 0.4));
        string body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("An unexpected server error occurred.", ReadError(body));
        Assert.DoesNotContain("secret internal detail", body);
    }

    [Theory]
    [InlineData("provider", "The AI provider is unavailable.")]
    [InlineData("unexpected", "An unexpected server error occurred.")]
    [InlineData("argument", "An unexpected server error occurred.")]
    public async Task FailureAfterFirstEvent_EmitsOneSanitizedNamedErrorWithoutCompletion(
        string failureType,
        string expectedError)
    {
        Exception exception = failureType switch
        {
            "provider" => new OllamaProviderException(
                OllamaFailureKind.Unavailable,
                "secret provider detail"),
            "argument" => new ArgumentException("secret argument detail"),
            _ => new InvalidOperationException("secret internal detail")
        };
        using var factory = new StreamingApiFactory(
            SuccessfulSearch(),
            new ControlledGenerationService(
                token => StreamThenThrow("partial", exception, token)));
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await PostStreamAsync(
            client,
            new RagRequest(TenantId, "question", 3, 0.4));
        string body = await response.Content.ReadAsStringAsync();
        List<SseItem<string>> events = ParseEvents(body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith("text/event-stream", response.Content.Headers.ContentType?.ToString());
        Assert.Equal(2, events.Count);
        Assert.Equal(SseParser.EventTypeDefault, events[0].EventType);
        Assert.Equal("partial", ReadStreamEvent(events[0]).Text);
        Assert.False(ReadStreamEvent(events[0]).IsCompleted);
        Assert.Equal("error", events[1].EventType);
        Assert.Equal(expectedError, ReadError(events[1].Data));
        Assert.DoesNotContain("secret", body);
        Assert.DoesNotContain(events, item =>
            item.EventType == SseParser.EventTypeDefault &&
            ReadStreamEvent(item).IsCompleted);
    }

    [Fact]
    public async Task ClientDisconnect_DisposesActiveStream()
    {
        var generation = new CancellationTrackingGenerationService();
        using var factory = new StreamingApiFactory(SuccessfulSearch(), generation);
        using HttpClient client = factory.CreateClient();
        using var request = CreateRequest(
            new RagRequest(TenantId, "question", 3, 0.4));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        using HttpResponseMessage response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellation.Token);
        await using Stream body = await response.Content.ReadAsStreamAsync();
        await using IAsyncEnumerator<SseItem<string>> events = SseParser
            .Create(body)
            .EnumerateAsync(cancellation.Token)
            .GetAsyncEnumerator(cancellation.Token);

        Assert.True(await events.MoveNextAsync());
        Assert.Equal("partial", ReadStreamEvent(events.Current).Text);

        await events.DisposeAsync();
        await body.DisposeAsync();
        response.Dispose();
        client.CancelPendingRequests();
        await generation.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static ControlledSearchService SuccessfulSearch()
    {
        return new ControlledSearchService((request, _) =>
            Task.FromResult(new SemanticSearchResponse(
                request.TenantId,
                request.Query,
                request.TopK ?? 5,
                request.MinSimilarity ?? 0.3,
                [new SemanticSearchMatch(
                    Guid.Parse("22222222-2222-2222-2222-222222222222"),
                    Guid.Parse("33333333-3333-3333-3333-333333333333"),
                    "Grounding document",
                    null,
                    0,
                    "evidence",
                    0.9)])));
    }

    private static async Task<HttpResponseMessage> PostStreamAsync(
        HttpClient client,
        RagRequest request)
    {
        using HttpRequestMessage message = CreateRequest(request);
        return await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead);
    }

    private static HttpRequestMessage CreateRequest(RagRequest request)
    {
        return new HttpRequestMessage(HttpMethod.Post, "/api/v1/ask/stream")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(request),
                Encoding.UTF8,
                "application/json")
        };
    }

    private static List<SseItem<string>> ParseEvents(string body)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(body));
        var events = new List<SseItem<string>>();
        IAsyncEnumerator<SseItem<string>> enumerator = SseParser
            .Create(stream)
            .EnumerateAsync()
            .GetAsyncEnumerator();

        try
        {
            while (enumerator.MoveNextAsync().AsTask().GetAwaiter().GetResult())
                events.Add(enumerator.Current);
        }
        finally
        {
            enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        return events;
    }

    private static RagStreamEvent ReadStreamEvent(SseItem<string> item)
    {
        return JsonSerializer.Deserialize<RagStreamEvent>(
            item.Data,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }

    private static string? ReadError(string body)
    {
        using JsonDocument document = JsonDocument.Parse(body);
        JsonElement root = document.RootElement;
        Assert.Single(root.EnumerateObject());
        return root.GetProperty("error").GetString();
    }

    private static async IAsyncEnumerable<string> EmptyStream(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        yield break;
    }

    private static async IAsyncEnumerable<string> StreamThenThrow(
        string fragment,
        Exception exception,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        yield return fragment;
        await Task.Yield();
        throw exception;
    }

    private sealed class StreamingApiFactory(
        ISemanticSearchService searchService,
        ITextGenerationService generationService)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("App:RateLimiting:Enabled", "false");
            builder.UseSetting("OpenTelemetry:ExportEnabled", "false");
            builder.ConfigureLogging(logging => logging.ClearProviders());
            builder.ConfigureTestServices(services =>
            {
                services.AddContractTenantStore();
                services.RemoveAll<ISemanticSearchService>();
                services.RemoveAll<ITextGenerationService>();
                services.AddSingleton(searchService);
                services.AddSingleton(generationService);
            });
        }

        protected override void ConfigureClient(HttpClient client)
        {
            base.ConfigureClient(client);
            client.DefaultRequestHeaders.Add("X-API-Key", TenantApiTestExtensions.KeyFor(TenantApiTestExtensions.ContractTenantId));
        }
    }

    private sealed class ControlledSearchService(
        Func<SemanticSearchRequest, CancellationToken, Task<SemanticSearchResponse>> search)
        : ISemanticSearchService
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public Task<SemanticSearchResponse> SearchAsync(
            SemanticSearchRequest request,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _callCount);
            return search(request, cancellationToken);
        }
    }

    private sealed class ControlledGenerationService(
        Func<CancellationToken, IAsyncEnumerable<string>> stream)
        : ITextGenerationService
    {
        public Task<string> GenerateAsync(
            string systemPrompt,
            string userPrompt,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public IAsyncEnumerable<string> GenerateStreamAsync(
            string systemPrompt,
            string userPrompt,
            CancellationToken cancellationToken = default)
        {
            return stream(cancellationToken);
        }
    }

    private sealed class CancellationTrackingGenerationService : ITextGenerationService
    {
        public TaskCompletionSource Disposed { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<string> GenerateAsync(
            string systemPrompt,
            string userPrompt,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public IAsyncEnumerable<string> GenerateStreamAsync(
            string systemPrompt,
            string userPrompt,
            CancellationToken cancellationToken = default)
        {
            return Stream(cancellationToken);
        }

        private async IAsyncEnumerable<string> Stream(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            try
            {
                yield return "partial";
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            finally
            {
                Disposed.TrySetResult();
            }
        }
    }
}
