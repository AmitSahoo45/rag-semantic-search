using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SemanticRagSearch.Api.Configuration;
using SemanticRagSearch.Api.Embedding;

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
    public async Task EmbedAsync_ThrowsWhenOllamaReturnsFailure()
    {
        var service = CreateService(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("model failed")
        }));

        InvalidOperationException exception =
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.EmbedAsync("hello"));

        Assert.Contains("500", exception.Message);
    }

    [Fact]
    public async Task EmbedAsync_ThrowsWhenDimensionDoesNotMatchConfiguration()
    {
        var service = CreateService(_ => Task.FromResult(JsonResponse("""{"embeddings":[[0.1,0.2]]}""")));

        InvalidOperationException exception =
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.EmbedAsync("hello"));

        Assert.Contains("dimension 2", exception.Message);
        Assert.Contains("3 was expected", exception.Message);
    }

    [Fact]
    public async Task EmbedBatchAsync_ThrowsWhenEmbeddingCountDoesNotMatchInputCount()
    {
        var service = CreateService(_ => Task.FromResult(JsonResponse("""{"embeddings":[[0.1,0.2,0.3]]}""")));

        InvalidOperationException exception =
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.EmbedBatchAsync(["one", "two"]));

        Assert.Contains("1 embeddings for 2 input texts", exception.Message);
    }

    private static OllamaEmbeddingService CreateService(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
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
        Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/api/embed", request.RequestUri?.AbsolutePath);

            return handler(request);
        }
    }
}
