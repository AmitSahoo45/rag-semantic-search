using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using SemanticRagSearch.Api.Configuration;

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
        using HttpResponseMessage response = await client.PostAsJsonAsync("/api/embed", request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            string errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"Ollama embedding request failed with status {(int)response.StatusCode} ({response.ReasonPhrase}). Response: {errorBody}");
        }

        OllamaEmbedResponse? ollamaResponse =
            await response.Content.ReadFromJsonAsync<OllamaEmbedResponse>(cancellationToken);

        if (ollamaResponse?.Embeddings is null || ollamaResponse.Embeddings.Count == 0)
            throw new InvalidOperationException("Ollama did not return any embeddings.");

        if (ollamaResponse.Embeddings.Count != texts.Count)
            throw new InvalidOperationException($"Ollama returned {ollamaResponse.Embeddings.Count} embeddings for {texts.Count} input texts.");

        for (int i = 0; i < ollamaResponse.Embeddings.Count; i++)
        {
            float[] embedding = ollamaResponse.Embeddings[i];

            if (embedding.Length != _options.EmbeddingDimension)
                throw new InvalidOperationException($"Embedding at index {i} has dimension {embedding.Length}, but {_options.EmbeddingDimension} was expected.");
        }

        _logger.LogInformation(
            "Generated {EmbeddingCount} embedding(s) using {EmbeddingModel} in {ElapsedMilliseconds} ms.",
            ollamaResponse.Embeddings.Count,
            _options.EmbeddingModel,
            (DateTime.UtcNow - startedAt).TotalMilliseconds);

        return ollamaResponse.Embeddings;
    }

    private static void ValidateText(string text, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("Text cannot be empty.", parameterName);
    }

    private sealed record OllamaEmbedRequest(string Model, object Input);

    private sealed record OllamaEmbedResponse(string? Model, List<float[]>? Embeddings);
}
