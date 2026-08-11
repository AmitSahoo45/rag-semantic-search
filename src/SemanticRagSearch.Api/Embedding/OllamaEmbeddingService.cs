using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SemanticRagSearch.Api.Configuration;
using SemanticRagSearch.Api.Ollama;

namespace SemanticRagSearch.Api.Embedding;

public class OllamaEmbeddingService : IEmbeddingService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly OllamaSettings _options;
    private readonly ILogger<OllamaEmbeddingService> _logger;

    public OllamaEmbeddingService(
        IHttpClientFactory httpClientFactory,
        IOptions<OllamaSettings> options,
        ILogger<OllamaEmbeddingService> logger)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default)
    {
        ValidateText(text, nameof(text));

        IReadOnlyList<float[]> embeddings = await RequestEmbeddingsAsync([text], cancellationToken);
        return embeddings[0];
    }

    public async Task<IReadOnlyList<float[]>> EmbedBatchAsync(
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken = default)
    {
        if (texts is null)
            throw new ArgumentNullException(nameof(texts));

        if (texts.Count == 0)
            throw new ArgumentException("At least one text value is required.", nameof(texts));

        for (int i = 0; i < texts.Count; i++)
        {
            ValidateText(texts[i], $"{nameof(texts)}[{i}]");
        }

        return await RequestEmbeddingsAsync(texts, cancellationToken);
    }

    private async Task<IReadOnlyList<float[]>> RequestEmbeddingsAsync(
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken)
    {
        HttpClient client = _httpClientFactory.CreateClient("Ollama");
        object input = texts.Count == 1 ? texts[0] : texts;
        var request = new OllamaEmbedRequest(_options.EmbeddingModel, input);

        DateTime startedAt = DateTime.UtcNow;
        HttpResponseMessage response;

        try
        {
            response = await client.PostAsJsonAsync(
                "/api/embed",
                request,
                cancellationToken);
        }
        catch (OperationCanceledException exception)
            when (!cancellationToken.IsCancellationRequested)
        {
            throw new OllamaProviderException(
                OllamaFailureKind.Timeout,
                "The Ollama embedding request timed out.",
                innerException: exception);
        }
        catch (HttpRequestException exception)
        {
            throw new OllamaProviderException(
                OllamaFailureKind.Unavailable,
                "The Ollama embedding service is unavailable.",
                innerException: exception);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new OllamaProviderException(
                    OllamaFailureKind.InvalidResponse,
                    $"The Ollama embedding request returned HTTP {(int)response.StatusCode}.",
                    response.StatusCode);
            }

            OllamaEmbedResponse? ollamaResponse;

            try
            {
                ollamaResponse =
                    await response.Content.ReadFromJsonAsync<OllamaEmbedResponse>(
                        cancellationToken);
            }
            catch (OperationCanceledException exception)
                when (!cancellationToken.IsCancellationRequested)
            {
                throw new OllamaProviderException(
                    OllamaFailureKind.Timeout,
                    "The Ollama embedding response timed out.",
                    innerException: exception);
            }
            catch (JsonException exception)
            {
                throw new OllamaProviderException(
                    OllamaFailureKind.InvalidResponse,
                    "The Ollama embedding response contained malformed JSON.",
                    innerException: exception);
            }
            catch (NotSupportedException exception)
            {
                throw new OllamaProviderException(
                    OllamaFailureKind.InvalidResponse,
                    "The Ollama embedding response used an unsupported format.",
                    innerException: exception);
            }
            catch (IOException exception)
            {
                throw new OllamaProviderException(
                    OllamaFailureKind.Unavailable,
                    "The Ollama embedding response was interrupted.",
                    innerException: exception);
            }

            if (ollamaResponse?.Embeddings is null ||
                ollamaResponse.Embeddings.Count == 0)
            {
                throw new OllamaProviderException(
                    OllamaFailureKind.InvalidResponse,
                    "Ollama did not return any embeddings.");
            }

            if (ollamaResponse.Embeddings.Count != texts.Count)
            {
                throw new OllamaProviderException(
                    OllamaFailureKind.InvalidResponse,
                    $"Ollama returned {ollamaResponse.Embeddings.Count} embeddings for {texts.Count} input texts.");
            }

            for (int i = 0; i < ollamaResponse.Embeddings.Count; i++)
            {
                float[]? embedding = ollamaResponse.Embeddings[i];

                if (embedding is null)
                {
                    throw new OllamaProviderException(
                        OllamaFailureKind.InvalidResponse,
                        $"Embedding at index {i} was null.");
                }

                if (embedding.Length != _options.EmbeddingDimension)
                {
                    throw new OllamaProviderException(
                        OllamaFailureKind.InvalidResponse,
                        $"Embedding at index {i} has dimension {embedding.Length}, but {_options.EmbeddingDimension} was expected.");
                }
            }

            _logger.LogInformation(
                "Generated {EmbeddingCount} embedding(s) using {EmbeddingModel} in {ElapsedMilliseconds} ms.",
                ollamaResponse.Embeddings.Count,
                _options.EmbeddingModel,
                (DateTime.UtcNow - startedAt).TotalMilliseconds);

            return ollamaResponse.Embeddings;
        }
    }

    private static void ValidateText(string text, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("Text cannot be empty.", parameterName);
    }

    private sealed record OllamaEmbedRequest(string Model, object Input);

    private sealed record OllamaEmbedResponse(string? Model, List<float[]>? Embeddings);
}
