using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Polly.CircuitBreaker;
using Polly.Timeout;
using SemanticRagSearch.Api.Configuration;
using SemanticRagSearch.Api.Ollama;
using SemanticRagSearch.Api.Resilience;

namespace SemanticRagSearch.Api.Generation;

public class OllamaTextGenerationService : ITextGenerationService
{
    private static readonly JsonSerializerOptions StreamingJsonOptions =
        new(JsonSerializerDefaults.Web);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly OllamaSettings _settings;
    private readonly ILogger<OllamaTextGenerationService> _logger;
    private readonly TimeSpan _totalRequestTimeout;
    private readonly TimeSpan _streamingIdleTimeout;
    private readonly TimeProvider _timeProvider;

    public OllamaTextGenerationService(
        IHttpClientFactory httpClientFactory,
        IOptions<OllamaSettings> settings,
        ILogger<OllamaTextGenerationService> logger,
        IOptions<OllamaResilienceOptions>? resilienceOptions = null,
        TimeProvider? timeProvider = null)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _settings = settings?.Value ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        OllamaResilienceOptions resilience =
            resilienceOptions?.Value ?? new OllamaResilienceOptions();
        _totalRequestTimeout = resilience.TotalRequestTimeout;
        _streamingIdleTimeout = resilience.StreamingIdleTimeout;
        _timeProvider = timeProvider ?? TimeProvider.System;

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
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/api/chat")
        {
            Content = JsonContent.Create(request)
        };
        using var deadline = new OllamaRequestDeadline(
            _timeProvider,
            _totalRequestTimeout,
            cancellationToken);

        DateTime startedAt = DateTime.UtcNow;
        using HttpResponseMessage response = await SendRequestAsync(
            client,
            httpRequest,
            HttpCompletionOption.ResponseContentRead,
            deadline.Token,
            cancellationToken);

        ThrowIfRequestFailed(response);

        OllamaChatResponse? ollamaResponse;

        try
        {
            ollamaResponse =
                await response.Content.ReadFromJsonAsync<OllamaChatResponse>(
                    deadline.Token);
        }
        catch (OperationCanceledException exception)
            when (!cancellationToken.IsCancellationRequested)
        {
            throw CreateTimeoutException("chat response", exception);
        }
        catch (OperationCanceledException exception)
            when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(
                exception.Message,
                exception,
                cancellationToken);
        }
        catch (JsonException exception)
        {
            throw new OllamaProviderException(
                OllamaFailureKind.InvalidResponse,
                "The Ollama chat response contained malformed JSON.",
                innerException: exception);
        }
        catch (NotSupportedException exception)
        {
            throw new OllamaProviderException(
                OllamaFailureKind.InvalidResponse,
                "The Ollama chat response used an unsupported format.",
                innerException: exception);
        }
        catch (IOException exception)
        {
            throw CreateUnavailableException("chat response", exception);
        }

        if (ollamaResponse?.Error is not null)
        {
            throw new OllamaProviderException(
                OllamaFailureKind.InvalidResponse,
                "The Ollama chat response reported an error.");
        }

        string? answer = ollamaResponse?.Message?.Content;

        if (string.IsNullOrWhiteSpace(answer))
        {
            throw new OllamaProviderException(
                OllamaFailureKind.InvalidResponse,
                "Ollama did not return a chat response.");
        }

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

        using HttpResponseMessage response = await SendRequestAsync(
            client,
            httpRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken,
            cancellationToken);

        ThrowIfRequestFailed(response);

        Stream responseStream;

        try
        {
            responseStream = await ExecuteWithStreamingIdleTimeoutAsync(
                response.Content.ReadAsStreamAsync,
                cancellationToken);
        }
        catch (OperationCanceledException exception)
            when (!cancellationToken.IsCancellationRequested)
        {
            throw CreateTimeoutException("streaming response", exception);
        }
        catch (HttpRequestException exception)
        {
            throw CreateUnavailableException("streaming response", exception);
        }
        catch (IOException exception)
        {
            throw CreateUnavailableException("streaming response", exception);
        }

        await using (responseStream)
        {
            using var reader = new StreamReader(responseStream);

            while (true)
            {
                string? line;

                try
                {
                    line = await ExecuteWithStreamingIdleTimeoutAsync(
                        token => reader.ReadLineAsync(token).AsTask(),
                        cancellationToken);
                }
                catch (OperationCanceledException exception)
                    when (!cancellationToken.IsCancellationRequested)
                {
                    throw CreateTimeoutException("streaming response", exception);
                }
                catch (IOException exception)
                {
                    throw CreateUnavailableException("streaming response", exception);
                }

                if (line is null)
                    break;

                if (string.IsNullOrWhiteSpace(line))
                    continue;

                OllamaChatResponse chunk = ParseStreamingResponse(line);

                if (chunk.Error is not null)
                {
                    throw new OllamaProviderException(
                        OllamaFailureKind.InvalidResponse,
                        "The Ollama streaming response reported an error.");
                }

                string? content = chunk.Message?.Content;
                if (!string.IsNullOrEmpty(content))
                    yield return content;

                if (chunk.Done)
                    yield break;
            }

            throw new OllamaProviderException(
                OllamaFailureKind.InvalidResponse,
                "The Ollama streaming response ended before completion.");
        }
    }

    private static void ValidatePrompt(string prompt, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            throw new ArgumentException("Prompt cannot be empty.", parameterName);
    }

    private static async Task<HttpResponseMessage> SendRequestAsync(
        HttpClient client,
        HttpRequestMessage request,
        HttpCompletionOption completionOption,
        CancellationToken requestCancellationToken,
        CancellationToken callerCancellationToken)
    {
        try
        {
            return await client.SendAsync(
                request,
                completionOption,
                requestCancellationToken);
        }
        catch (TimeoutRejectedException exception)
        {
            throw CreateTimeoutException("request", exception);
        }
        catch (BrokenCircuitException exception)
        {
            throw CreateUnavailableException("request", exception);
        }
        catch (OperationCanceledException exception)
            when (!callerCancellationToken.IsCancellationRequested)
        {
            throw CreateTimeoutException("request", exception);
        }
        catch (OperationCanceledException exception)
            when (callerCancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(
                exception.Message,
                exception,
                callerCancellationToken);
        }
        catch (HttpRequestException exception)
        {
            throw CreateUnavailableException("request", exception);
        }
    }

    private async Task<T> ExecuteWithStreamingIdleTimeoutAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        using var idleCancellation = new CancellationTokenSource();
        using var readCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            idleCancellation.Token);
        using ITimer timer = _timeProvider.CreateTimer(
            static state =>
            {
                try
                {
                    ((CancellationTokenSource)state!).Cancel();
                }
                catch (ObjectDisposedException)
                {
                }
            },
            idleCancellation,
            _streamingIdleTimeout,
            Timeout.InfiniteTimeSpan);

        try
        {
            return await operation(readCancellation.Token);
        }
        catch (OperationCanceledException exception)
            when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(
                exception.Message,
                exception,
                cancellationToken);
        }
        catch (OperationCanceledException exception)
            when (idleCancellation.IsCancellationRequested)
        {
            throw CreateTimeoutException("streaming response", exception);
        }
    }

    private static void ThrowIfRequestFailed(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
            return;

        throw new OllamaProviderException(
            OllamaFailureKind.InvalidResponse,
            $"The Ollama chat request returned HTTP {(int)response.StatusCode}.",
            response.StatusCode);
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
            throw new OllamaProviderException(
                OllamaFailureKind.InvalidResponse,
                "Ollama returned malformed streaming JSON.",
                innerException: exception);
        }
    }

    private static OllamaProviderException CreateTimeoutException(
        string operation,
        Exception exception)
    {
        return new OllamaProviderException(
            OllamaFailureKind.Timeout,
            $"The Ollama {operation} timed out.",
            innerException: exception);
    }

    private static OllamaProviderException CreateUnavailableException(
        string operation,
        Exception exception)
    {
        return new OllamaProviderException(
            OllamaFailureKind.Unavailable,
            $"The Ollama {operation} was interrupted.",
            innerException: exception);
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
