using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using SemanticRagSearch.Api.Ingestion;
using SemanticRagSearch.Api.Ollama;
using SemanticRagSearch.Api.Rag;
using SemanticRagSearch.Api.Search;

namespace SemanticRagSearch.IntegrationTests;

public class ProviderErrorApiTests
{
    private static readonly string[] NonStreamingEndpoints =
    [
        "/api/v1/documents",
        "/api/v1/search",
        "/api/v1/ask"
    ];

    [Theory]
    [MemberData(nameof(GetNonStreamingEndpoints))]
    public async Task NonStreamingEndpoints_ReturnSameSanitizedProviderError(
        string endpoint)
    {
        using var factory = new ThrowingApiFactory(
            new OllamaProviderException(
                OllamaFailureKind.InvalidResponse,
                "secret provider body",
                HttpStatusCode.InternalServerError));
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await PostAsync(client, endpoint);
        string body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal(
            "The AI provider returned an invalid response.",
            ReadError(body));
        Assert.DoesNotContain("secret provider body", body);
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
    public async Task ProviderFailureKinds_MapToStableHttpContract(
        OllamaFailureKind failureKind,
        HttpStatusCode expectedStatus,
        string expectedError)
    {
        using var factory = new ThrowingApiFactory(
            new OllamaProviderException(
                failureKind,
                "secret provider body"));
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response =
            await PostAsync(client, "/api/v1/search");
        string body = await response.Content.ReadAsStringAsync();

        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.Equal(expectedError, ReadError(body));
        Assert.DoesNotContain("secret provider body", body);
    }

    [Fact]
    public async Task UnexpectedFailure_ReturnsSanitizedInternalServerError()
    {
        using var factory = new ThrowingApiFactory(
            new InvalidOperationException("secret internal detail"));
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response =
            await PostAsync(client, "/api/v1/ask");
        string body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("An unexpected server error occurred.", ReadError(body));
        Assert.DoesNotContain("secret internal detail", body);
    }

    [Theory]
    [MemberData(nameof(GetNonStreamingEndpoints))]
    public async Task ExistingArgumentErrors_RemainBadRequest(string endpoint)
    {
        using var factory = new ThrowingApiFactory(
            new ArgumentException("invalid request"));
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await PostAsync(client, endpoint);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            "invalid request",
            ReadError(await response.Content.ReadAsStringAsync()));
    }

    [Theory]
    [MemberData(nameof(GetNonStreamingEndpoints))]
    public async Task ExistingMissingTenantErrors_RemainNotFound(string endpoint)
    {
        using var factory = new ThrowingApiFactory(
            new KeyNotFoundException("tenant missing"));
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await PostAsync(client, endpoint);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(
            "tenant missing",
            ReadError(await response.Content.ReadAsStringAsync()));
    }

    public static IEnumerable<object[]> GetNonStreamingEndpoints()
    {
        return NonStreamingEndpoints.Select(endpoint => new object[] { endpoint });
    }

    private static async Task<HttpResponseMessage> PostAsync(
        HttpClient client,
        string endpoint)
    {
        string json = endpoint switch
        {
            "/api/v1/documents" =>
                """
                {
                  "tenantId": "11111111-1111-1111-1111-111111111111",
                  "title": "Document",
                  "sourceUrl": null,
                  "content": "Grounded content"
                }
                """,
            "/api/v1/search" =>
                """
                {
                  "tenantId": "11111111-1111-1111-1111-111111111111",
                  "query": "question",
                  "topK": 3,
                  "minSimilarity": 0.4
                }
                """,
            "/api/v1/ask" =>
                """
                {
                  "tenantId": "11111111-1111-1111-1111-111111111111",
                  "question": "question",
                  "topK": 3,
                  "minSimilarity": 0.4
                }
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(endpoint))
        };
        using var content = new StringContent(
            json,
            Encoding.UTF8,
            "application/json");

        return await client.PostAsync(endpoint, content);
    }

    private static string? ReadError(string body)
    {
        using JsonDocument document = JsonDocument.Parse(body);
        JsonElement root = document.RootElement;
        Assert.Single(root.EnumerateObject());
        return root.GetProperty("error").GetString();
    }

    private sealed class ThrowingApiFactory(Exception exception)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureLogging(logging => logging.ClearProviders());
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IDocumentIngestionService>();
                services.RemoveAll<ISemanticSearchService>();
                services.RemoveAll<IRagService>();
                services.AddSingleton<IDocumentIngestionService>(
                    new ThrowingDocumentIngestionService(exception));
                services.AddSingleton<ISemanticSearchService>(
                    new ThrowingSemanticSearchService(exception));
                services.AddSingleton<IRagService>(
                    new ThrowingRagService(exception));
            });
        }
    }

    private sealed class ThrowingDocumentIngestionService(Exception exception)
        : IDocumentIngestionService
    {
        public Task<DocumentIngestionResult> IngestAsync(
            DocumentIngestionRequest request,
            CancellationToken cancellationToken = default)
        {
            return Task.FromException<DocumentIngestionResult>(exception);
        }
    }

    private sealed class ThrowingSemanticSearchService(Exception exception)
        : ISemanticSearchService
    {
        public Task<SemanticSearchResponse> SearchAsync(
            SemanticSearchRequest request,
            CancellationToken cancellationToken = default)
        {
            return Task.FromException<SemanticSearchResponse>(exception);
        }
    }

    private sealed class ThrowingRagService(Exception exception) : IRagService
    {
        public Task<RagResponse> AskAsync(
            RagRequest request,
            CancellationToken cancellationToken = default)
        {
            return Task.FromException<RagResponse>(exception);
        }

        public IAsyncEnumerable<RagStreamEvent> AskStreamAsync(
            RagRequest request,
            CancellationToken cancellationToken = default)
        {
            return ThrowAsync(cancellationToken);
        }

        private async IAsyncEnumerable<RagStreamEvent> ThrowAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield return await Task.FromException<RagStreamEvent>(exception);
        }
    }
}
