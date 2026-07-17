using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SemanticRagSearch.Api.Configuration;

namespace SemanticRagSearch.Api.Generation;

public class OllamaTextGenerationService : ITextGenerationService
{
    private static readonly JsonSerializerOptions StreamingJsonOptions =
        new(JsonSerializerDefaults.Web);

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

        await ThrowIfRequestFailedAsync(response, cancellationToken);

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

    public async IAsyncEnumerable<string> GenerateStreamAsync(
        string systemPrompt,
        string userPrompt,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
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
            true);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/api/chat")
        {
            Content = JsonContent.Create(request)
        };

        using HttpResponseMessage response = await client.SendAsync(
            httpRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        await ThrowIfRequestFailedAsync(response, cancellationToken);

        await using Stream responseStream =
            await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(responseStream);

        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            OllamaChatResponse chunk = ParseStreamingResponse(line);

            if (chunk.Error is not null)
            {
                string errorDetails = string.IsNullOrWhiteSpace(chunk.Error)
                    ? "No error details were provided."
                    : chunk.Error;

                throw new InvalidOperationException(
                    $"Ollama streaming request failed. Response: {errorDetails}");
            }

            string? content = chunk.Message?.Content;
            if (!string.IsNullOrEmpty(content))
                yield return content;

            if (chunk.Done)
                yield break;
        }
    }

    private static void ValidatePrompt(string prompt, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            throw new ArgumentException("Prompt cannot be empty.", parameterName);
    }

    private static async Task ThrowIfRequestFailedAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
            return;

        string errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new InvalidOperationException(
            $"Ollama chat request failed with status {(int)response.StatusCode} " +
            $"({response.ReasonPhrase}). Response: {errorBody}");
    }

    private static OllamaChatResponse ParseStreamingResponse(string line)
    {
        try
        {
            return JsonSerializer.Deserialize<OllamaChatResponse>(
                    line,
                    StreamingJsonOptions)
                ?? throw new JsonException("The streaming record was null.");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                "Ollama returned malformed streaming JSON.",
                exception);
        }
    }

    private sealed record OllamaChatRequest(
        string Model,
        IReadOnlyList<OllamaChatMessage> Messages,
        bool Stream);

    private sealed record OllamaChatMessage(string Role, string Content);

    private sealed record OllamaChatResponse(
        OllamaChatMessage? Message,
        bool Done = false,
        string? Error = null);
}
