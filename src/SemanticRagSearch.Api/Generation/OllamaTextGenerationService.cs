using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using SemanticRagSearch.Api.Configuration;

namespace SemanticRagSearch.Api.Generation;

public class OllamaTextGenerationService : ITextGenerationService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly OllamaSettings _settings;
    private readonly ILogger<OllamaTextGenerationService> _logger;

    public OllamaTextGenerationService(
        IHttpClientFactory httpClientFactory,
        IOptions<OllamaSettings> settings,
        ILogger<OllamaTextGenerationService> logger)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _settings = settings?.Value ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        if (string.IsNullOrWhiteSpace(_settings.ChatModel))
            throw new InvalidOperationException("Ollama chat model is not configured.");
    }

    public async Task<string> GenerateAsync(
        string systemPrompt,
        string userPrompt,
        CancellationToken cancellationToken = default)
    {
        ValidatePrompt(systemPrompt, nameof(systemPrompt));
        ValidatePrompt(userPrompt, nameof(userPrompt));

        HttpClient client = _httpClientFactory.CreateClient("Ollama");
        var request = new OllamaChatRequest(
            _settings.ChatModel,
            [
                new OllamaChatMessage("system", systemPrompt),
                new OllamaChatMessage("user", userPrompt)
            ],
            false);

        DateTime startedAt = DateTime.UtcNow;
        using HttpResponseMessage response = await client.PostAsJsonAsync("/api/chat", request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            string errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"Ollama chat request failed with status {(int)response.StatusCode} ({response.ReasonPhrase}). Response: {errorBody}");
        }

        OllamaChatResponse? ollamaResponse =
            await response.Content.ReadFromJsonAsync<OllamaChatResponse>(cancellationToken);

        string? answer = ollamaResponse?.Message?.Content;

        if (string.IsNullOrWhiteSpace(answer))
            throw new InvalidOperationException("Ollama did not return a chat response.");

        _logger.LogInformation(
            "Generated RAG answer using {ChatModel} in {ElapsedMilliseconds} ms.",
            _settings.ChatModel,
            (DateTime.UtcNow - startedAt).TotalMilliseconds);

        return answer.Trim();
    }

    private static void ValidatePrompt(string prompt, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            throw new ArgumentException("Prompt cannot be empty.", parameterName);
    }

    private sealed record OllamaChatRequest(
        string Model,
        IReadOnlyList<OllamaChatMessage> Messages,
        bool Stream);

    private sealed record OllamaChatMessage(string Role, string Content);

    private sealed record OllamaChatResponse(OllamaChatMessage? Message);
}
