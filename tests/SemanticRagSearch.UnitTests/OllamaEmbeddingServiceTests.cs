using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SemanticRagSearch.Api.Configuration;
using SemanticRagSearch.Api.Embedding;
using SemanticRagSearch.Api.Ollama;

namespace SemanticRagSearch.UnitTests;

public class OllamaEmbeddingServiceTests
{
    [Fact]
    public async Task EmbedAsync_PostsTextToOllamaAndReturnsEmbedding()
    {
        string? requestJson = null;
        var service = CreateService(async request =>
        {
            requestJson = await request.Content!.ReadAsStringAsync();
            return JsonResponse("""{"model":"nomic-embed-text","embeddings":[[0.1,0.2,0.3]]}""");
        });

        float[] embedding = await service.EmbedAsync("hello world");

        Assert.Equal(new[] { 0.1f, 0.2f, 0.3f }, embedding);

        using JsonDocument requestDocument = JsonDocument.Parse(requestJson!);
        Assert.Equal("nomic-embed-text", requestDocument.RootElement.GetProperty("model").GetString());
        Assert.Equal("hello world", requestDocument.RootElement.GetProperty("input").GetString());
    }

    [Fact]
    public async Task EmbedBatchAsync_PostsTextsToOllamaAndReturnsEmbeddings()
    {
        string? requestJson = null;
        var service = CreateService(async request =>
        {
            requestJson = await request.Content!.ReadAsStringAsync();
            return JsonResponse("""{"model":"nomic-embed-text","embeddings":[[0.1,0.2,0.3],[0.4,0.5,0.6]]}""");
        });

        IReadOnlyList<float[]> embeddings = await service.EmbedBatchAsync(["first chunk", "second chunk"]);

        Assert.Equal(2, embeddings.Count);
        Assert.Equal(new[] { 0.1f, 0.2f, 0.3f }, embeddings[0]);
        Assert.Equal(new[] { 0.4f, 0.5f, 0.6f }, embeddings[1]);

        using JsonDocument requestDocument = JsonDocument.Parse(requestJson!);
        JsonElement input = requestDocument.RootElement.GetProperty("input");
        Assert.Equal(JsonValueKind.Array, input.ValueKind);
        Assert.Equal("first chunk", input[0].GetString());
        Assert.Equal("second chunk", input[1].GetString());
    }

    [Fact]
    public async Task EmbedAsync_ThrowsWhenTextIsBlank()
    {
        var service = CreateService(_ => Task.FromResult(JsonResponse("""{"embeddings":[[0.1,0.2,0.3]]}""")));

        await Assert.ThrowsAsync<ArgumentException>(() => service.EmbedAsync("   "));
    }

    [Fact]
    public async Task EmbedAsync_ClassifiesHttpFailureWithoutProviderBody()
    {
        var service = CreateService(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("secret provider body")
        }));

        OllamaProviderException exception =
            await Assert.ThrowsAsync<OllamaProviderException>(() => service.EmbedAsync("hello"));

        Assert.Equal(OllamaFailureKind.InvalidResponse, exception.FailureKind);
        Assert.Equal(HttpStatusCode.InternalServerError, exception.UpstreamStatusCode);
        Assert.DoesNotContain("secret provider body", exception.Message);
    }

    [Fact]
    public async Task EmbedAsync_ClassifiesMalformedJsonAsInvalidResponse()
    {
        var service = CreateService(_ => Task.FromResult(JsonResponse("{not-json}")));

        OllamaProviderException exception =
            await Assert.ThrowsAsync<OllamaProviderException>(() => service.EmbedAsync("hello"));

        Assert.Equal(OllamaFailureKind.InvalidResponse, exception.FailureKind);
        Assert.IsType<JsonException>(exception.InnerException);
    }

    [Fact]
    public async Task EmbedAsync_ClassifiesNullEmbeddingAsInvalidResponse()
    {
        var service = CreateService(_ => Task.FromResult(
            JsonResponse("""{"embeddings":[null]}""")));

        OllamaProviderException exception =
            await Assert.ThrowsAsync<OllamaProviderException>(
                () => service.EmbedAsync("hello"));

        Assert.Equal(OllamaFailureKind.InvalidResponse, exception.FailureKind);
    }

    [Fact]
    public async Task EmbedAsync_ClassifiesNetworkFailureAsUnavailable()
    {
        var service = CreateService(
            (_, _) => throw new HttpRequestException("connection refused"));

        OllamaProviderException exception =
            await Assert.ThrowsAsync<OllamaProviderException>(() => service.EmbedAsync("hello"));

        Assert.Equal(OllamaFailureKind.Unavailable, exception.FailureKind);
        Assert.IsType<HttpRequestException>(exception.InnerException);
    }

    [Fact]
    public async Task EmbedAsync_ClassifiesProviderCancellationAsTimeout()
    {
        var service = CreateService(
            (_, _) => throw new OperationCanceledException(
                "provider timeout",
                new TimeoutException(),
                CancellationToken.None));

        OllamaProviderException exception =
            await Assert.ThrowsAsync<OllamaProviderException>(() => service.EmbedAsync("hello"));

        Assert.Equal(OllamaFailureKind.Timeout, exception.FailureKind);
        Assert.IsType<OperationCanceledException>(exception.InnerException);
    }

    [Fact]
    public async Task EmbedAsync_PreservesCallerCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var service = CreateService(
            (_, token) => Task.FromCanceled<HttpResponseMessage>(token));

        OperationCanceledException exception =
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => service.EmbedAsync("hello", cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.IsNotType<OllamaProviderException>(exception);
    }

    [Fact]
    public async Task EmbedAsync_ThrowsWhenDimensionDoesNotMatchConfiguration()
    {
        var service = CreateService(_ => Task.FromResult(JsonResponse("""{"embeddings":[[0.1,0.2]]}""")));

        OllamaProviderException exception =
            await Assert.ThrowsAsync<OllamaProviderException>(() => service.EmbedAsync("hello"));

        Assert.Equal(OllamaFailureKind.InvalidResponse, exception.FailureKind);
        Assert.Contains("dimension 2", exception.Message);
        Assert.Contains("3 was expected", exception.Message);
    }

    [Fact]
    public async Task EmbedBatchAsync_ThrowsWhenEmbeddingCountDoesNotMatchInputCount()
    {
        var service = CreateService(_ => Task.FromResult(JsonResponse("""{"embeddings":[[0.1,0.2,0.3]]}""")));

        OllamaProviderException exception =
            await Assert.ThrowsAsync<OllamaProviderException>(() => service.EmbedBatchAsync(["one", "two"]));

        Assert.Equal(OllamaFailureKind.InvalidResponse, exception.FailureKind);
        Assert.Contains("1 embeddings for 2 input texts", exception.Message);
    }

    private static OllamaEmbeddingService CreateService(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
    {
        return CreateService((request, _) => handler(request));
    }

    private static OllamaEmbeddingService CreateService(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
    {
        var httpClient = new HttpClient(new TestHttpMessageHandler(handler))
        {
            BaseAddress = new Uri("http://localhost:11434")
        };

        var httpClientFactory = new TestHttpClientFactory(httpClient);
        var options = Options.Create(new OllamaSettings
        {
            EmbeddingModel = "nomic-embed-text",
            EmbeddingDimension = 3
        });

        return new OllamaEmbeddingService(
            httpClientFactory,
            options,
            NullLogger<OllamaEmbeddingService>.Instance);
    }

    private static HttpResponseMessage JsonResponse(string json)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        };
    }

    private sealed class TestHttpClientFactory(HttpClient httpClient) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            Assert.Equal("Ollama", name);
            return httpClient;
        }
    }

    private sealed class TestHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/api/embed", request.RequestUri?.AbsolutePath);

            return handler(request, cancellationToken);
        }
    }
}
