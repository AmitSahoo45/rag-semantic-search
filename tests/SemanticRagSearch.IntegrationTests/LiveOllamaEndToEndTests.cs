using System.Net;
using System.Net.Http.Json;
using System.Net.ServerSentEvents;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using SemanticRagSearch.Api.Configuration;
using SemanticRagSearch.Api.Domain;
using SemanticRagSearch.Api.Ingestion;
using SemanticRagSearch.Api.Rag;
using SemanticRagSearch.Api.Search;
using Xunit.Abstractions;

namespace SemanticRagSearch.IntegrationTests;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Live")]
public sealed class LiveOllamaEndToEndTests(PostgresFixture database, ITestOutputHelper output)
{
    [LiveFact]
    public async Task RealOllama_IngestsRetrievesAnswersAndStreamsGroundedSources()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        CancellationToken token = deadline.Token;
        using var factory = new LiveApiFactory(database.ConnectionString, output);
        using HttpClient client = factory.CreateClient();
        client.Timeout = TimeSpan.FromMinutes(4);
        OllamaSettings settings = factory.Services.GetRequiredService<IOptions<OllamaSettings>>().Value;

        // Fail explicitly when opted in without both actual models; never replace the provider.
        using var ollama = new HttpClient { BaseAddress = new Uri(settings.BaseUrl), Timeout = TimeSpan.FromSeconds(15) };
        using JsonDocument tags = await ollama.GetFromJsonAsync<JsonDocument>("/api/tags", token)
            ?? throw new InvalidOperationException("Ollama returned no model list.");
        string[] models = tags.RootElement.GetProperty("models").EnumerateArray()
            .Select(model => model.GetProperty("name").GetString()!).ToArray();
        Assert.Contains(models, model => MatchesModel(model, settings.EmbeddingModel));
        Assert.Contains(models, model => MatchesModel(model, settings.ChatModel));
        using HttpResponseMessage health = await client.GetAsync("/health", token);
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);

        Guid tenantId = Guid.NewGuid();
        client.DefaultRequestHeaders.Add("X-API-Key", TenantApiTestExtensions.KeyFor(tenantId));
        await using (var context = database.CreateDbContext())
        {
            context.Tenants.Add(new Tenant
            {
                TenantId = tenantId,
                ApiKeyHash = TenantApiTestExtensions.HashFor(tenantId),
                TenantName = "Live verification",
                TenantDescription = "Isolated live-provider test tenant",
                Email = $"live-{tenantId:N}@example.test"
            });
            await context.SaveChangesAsync(token);
        }

        var document = new DocumentIngestionRequest(tenantId, "Project launch instructions", "https://example.test/live",
            "The verification project's launch code is COBALT-7319. The launch coordinator must use COBALT-7319 to authorize the launch.");
        using HttpResponseMessage created = await client.PostAsJsonAsync("/api/v1/documents", document, token);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        DocumentIngestionResult ingestion = (await created.Content.ReadFromJsonAsync<DocumentIngestionResult>(token))!;
        Assert.False(ingestion.WasDuplicate);
        Assert.True(ingestion.ChunkCount > 0);

        using HttpResponseMessage duplicate = await client.PostAsJsonAsync("/api/v1/documents", document, token);
        Assert.Equal(HttpStatusCode.OK, duplicate.StatusCode);
        DocumentIngestionResult repeated = (await duplicate.Content.ReadFromJsonAsync<DocumentIngestionResult>(token))!;
        Assert.True(repeated.WasDuplicate);
        Assert.Equal(ingestion.DocumentId, repeated.DocumentId);
        Assert.Equal(ingestion.ChunkCount, repeated.ChunkCount);

        await using (var connection = new NpgsqlConnection(database.ConnectionString))
        {
            await connection.OpenAsync(token);
            await using var command = new NpgsqlCommand(
                "SELECT count(*), min(vector_dims(embedding)), max(vector_dims(embedding)) FROM chunks WHERE document_id = @id", connection);
            command.Parameters.AddWithValue("id", ingestion.DocumentId);
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(token);
            Assert.True(await reader.ReadAsync(token));
            Assert.Equal((long)ingestion.ChunkCount, reader.GetInt64(0));
            Assert.Equal(768, reader.GetInt32(1));
            Assert.Equal(768, reader.GetInt32(2));
        }

        const string question = "What is the verification project's launch code?";
        using HttpResponseMessage searchResponse = await client.PostAsJsonAsync("/api/v1/search",
            new SemanticSearchRequest(tenantId, question, 3, 0.1), token);
        Assert.Equal(HttpStatusCode.OK, searchResponse.StatusCode);
        SemanticSearchResponse search = (await searchResponse.Content.ReadFromJsonAsync<SemanticSearchResponse>(token))!;
        Assert.NotEmpty(search.Matches);
        Assert.All(search.Matches, match => Assert.Equal(ingestion.DocumentId, match.DocumentId));

        var request = new RagRequest(tenantId, question, 3, 0.1);
        using HttpResponseMessage answerResponse = await client.PostAsJsonAsync("/api/v1/ask", request, token);
        Assert.Equal(HttpStatusCode.OK, answerResponse.StatusCode);
        RagResponse answer = (await answerResponse.Content.ReadFromJsonAsync<RagResponse>(token))!;
        output.WriteLine("RAG response: " + JsonSerializer.Serialize(answer));
        Assert.NotEmpty(answer.Sources);
        Assert.All(answer.Sources, source => Assert.Equal(ingestion.DocumentId, source.DocumentId));
        Assert.Contains(answer.Sources, source => source.TextContent.Contains("COBALT-7319", StringComparison.Ordinal));
        Assert.NotNull(answer.Metadata);

        using var streamRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/ask/stream")
        {
            Content = JsonContent.Create(request)
        };
        using HttpResponseMessage streamResponse = await client.SendAsync(streamRequest, HttpCompletionOption.ResponseHeadersRead, token);
        Assert.Equal(HttpStatusCode.OK, streamResponse.StatusCode);
        Assert.Equal("text/event-stream", streamResponse.Content.Headers.ContentType?.MediaType);
        await using Stream stream = await streamResponse.Content.ReadAsStreamAsync(token);
        var events = new List<RagStreamEvent>();
        await foreach (SseItem<string> item in SseParser.Create(stream).EnumerateAsync(token))
        {
            Assert.Equal(SseParser.EventTypeDefault, item.EventType);
            events.Add(JsonSerializer.Deserialize<RagStreamEvent>(item.Data, new JsonSerializerOptions(JsonSerializerDefaults.Web))!);
        }
        Assert.True(events.Count >= 2);
        string streamedAnswer = string.Concat(events.Select(item => item.Text));
        output.WriteLine("Streamed answer: " + streamedAnswer);
        RagStreamEvent completion = Assert.Single(events, item => item.IsCompleted);
        Assert.Same(events[^1], completion);
        Assert.NotEmpty(completion.Sources!);
        Assert.All(completion.Sources!, source => Assert.Equal(ingestion.DocumentId, source.DocumentId));
        Assert.NotNull(completion.Metadata);

        using HttpResponseMessage unrelatedResponse = await client.PostAsJsonAsync("/api/v1/ask",
            request with { Question = "What is the orbital period of Neptune?", MinSimilarity = 0.99 }, token);
        Assert.Equal(HttpStatusCode.OK, unrelatedResponse.StatusCode);
        RagResponse unrelated = (await unrelatedResponse.Content.ReadFromJsonAsync<RagResponse>(token))!;
        Assert.Empty(unrelated.Sources);
        Assert.Equal("I don't know based on the provided documents.", unrelated.Answer);
        Assert.Equal(0, unrelated.Metadata.GenerationLatencyMs);

        using HttpResponseMessage invalid = await client.PostAsJsonAsync("/api/v1/ask/stream", request with { Question = " " }, token);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal("application/json", invalid.Content.Headers.ContentType?.MediaType);
        using HttpResponseMessage missingTenant = await client.PostAsJsonAsync("/api/v1/ask/stream", request with { TenantId = Guid.NewGuid() }, token);
        Assert.Equal(HttpStatusCode.Forbidden, missingTenant.StatusCode);

        // Check both transports before evaluating the model's answers so failures retain full diagnostics.
        Assert.True(
            answer.Answer.Contains("COBALT-7319", StringComparison.OrdinalIgnoreCase) &&
            streamedAnswer.Contains("COBALT-7319", StringComparison.OrdinalIgnoreCase),
            $"Expected the ingested fact in both answers. Non-streaming: {answer.Answer}; streaming: {streamedAnswer}");
    }

    private static bool MatchesModel(string actual, string configured) =>
        string.Equals(actual, configured.Contains(':') ? configured : $"{configured}:latest", StringComparison.OrdinalIgnoreCase);

    private sealed class LiveApiFactory(string connectionString, ITestOutputHelper output) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("App:RateLimiting:Enabled", "true");
            builder.UseSetting("OpenTelemetry:ExportEnabled",
                Environment.GetEnvironmentVariable("RAG_EXPORT_TEST_TELEMETRY") == "1" ? "true" : "false");
            builder.UseSetting("OpenTelemetry:ServiceName", "SemanticRagSearch.Phase3Verification");
            builder.ConfigureLogging(logging => logging.ClearProviders());
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["ConnectionStrings:DefaultConnection"] = connectionString,
                    ["App:Ollama:BaseUrl"] = Environment.GetEnvironmentVariable("RAG_OLLAMA_BASE_URL") ?? "http://localhost:11434"
                }));
            builder.ConfigureServices(services => services.AddHttpClient("Ollama")
                .AddHttpMessageHandler(() => new RequestObserver(output)));
        }
    }

    private sealed class RequestObserver(ITestOutputHelper output) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsolutePath == "/api/chat" && request.Content is not null)
                output.WriteLine("Ollama chat request: " + await request.Content.ReadAsStringAsync(cancellationToken));
            return await base.SendAsync(request, cancellationToken);
        }
    }
}

public sealed class LiveFactAttribute : FactAttribute
{
    public LiveFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("RAG_RUN_LIVE_TESTS") != "1")
            Skip = "Set RAG_RUN_LIVE_TESTS=1 with Docker and real nomic-embed-text/llama3.2 models available.";
    }
}
