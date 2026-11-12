using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Pgvector;
using Pgvector.EntityFrameworkCore;
using SemanticRagSearch.Api.Chunking;
using SemanticRagSearch.Api.Configuration;
using SemanticRagSearch.Api.Data;
using SemanticRagSearch.Api.Domain;
using SemanticRagSearch.Api.Embedding;
using SemanticRagSearch.Api.Evaluation;
using SemanticRagSearch.Api.Ingestion;
using SemanticRagSearch.Api.Search;
using SemanticRagSearch.Api.VectorStore;

namespace SemanticRagSearch.Evaluation;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    // The corpus hash must not vary with Environment.NewLine, so indentation uses LF on every platform.
    private static readonly JsonSerializerOptions HashJsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true, NewLine = "\n" };

    public static async Task<int> Main(string[] args)
    {
        if (args.Contains("--help"))
        {
            Console.WriteLine("Run from repository root: dotnet run --project tools/SemanticRagSearch.Evaluation -- [--dataset samples/phase4/dataset.json] [--output artifacts/phase4] [--top-k 3] [--min-similarity 0.3]");
            Console.WriteLine("Environment: PHASE4_POSTGRES (connection with CREATE DATABASE permission), PHASE4_OLLAMA_URL, PHASE4_EMBEDDING_MODEL. Uses and removes a unique disposable database. Never downloads models.");
            return 0;
        }

        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        // SIGTERM (timeout, docker stop) and SIGHUP (closed console window) must reach the same cleanup as Ctrl+C.
        using PosixSignalRegistration sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => { context.Cancel = true; cancellation.Cancel(); });
        using PosixSignalRegistration sighup = PosixSignalRegistration.Create(PosixSignal.SIGHUP, context => { context.Cancel = true; cancellation.Cancel(); });
        try
        {
            EvaluationRunOptions options = EvaluationRunOptions.Parse(args);
            RetrievalEvaluationDataset dataset = await RetrievalEvaluationDatasetLoader.LoadAsync(options.DatasetPath, cancellation.Token);
            await RunAsync(options, dataset, cancellation.Token);
            return 0;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            Console.Error.WriteLine("Evaluation cancelled or exceeded 15 minutes; disposable database cleanup was attempted.");
            return 130;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // BCL messages name the offending path; a missing dataset or unwritable output directory is not a service failure.
            Console.Error.WriteLine("Dataset or output path error: " + exception.Message);
            return 2;
        }
        catch (Exception exception)
        {
            // Connection strings and provider response bodies do not belong in console reports.
            // Cancellations not requested by this tool (HttpClient timeouts, the 30 s cleanup deadline) land here with their type named.
            Console.Error.WriteLine(exception is ArgumentException or InvalidDataException or InvalidOperationException
                ? exception.Message
                : $"Evaluation failed ({exception.GetType().Name}). Check PostgreSQL CREATE DATABASE permission, installed Ollama model, and service availability.");
            return 1;
        }
    }

    private static async Task RunAsync(EvaluationRunOptions options, RetrievalEvaluationDataset dataset, CancellationToken ct)
    {
        string connectionString = Environment.GetEnvironmentVariable("PHASE4_POSTGRES")
            ?? "Host=localhost;Port=5432;Database=ragdb;Username=raguser;Password=ragpass";
        var adminSettings = new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false };
        string databaseName = "phase4_eval_" + Guid.NewGuid().ToString("N");
        var evaluationSettings = new NpgsqlConnectionStringBuilder(connectionString) { Database = databaseName, Pooling = false };
        var ollama = new OllamaSettings
        {
            BaseUrl = Environment.GetEnvironmentVariable("PHASE4_OLLAMA_URL") ?? "http://localhost:11434",
            EmbeddingModel = Environment.GetEnvironmentVariable("PHASE4_EMBEDDING_MODEL") ?? "nomic-embed-text"
        };
        var searchSettings = new SearchSettings();
        var chunkingSettings = new ChunkingSettings();
        using var services = new ServiceCollection().AddHttpClient("Ollama", client =>
        {
            client.BaseAddress = new Uri(ollama.BaseUrl);
            client.Timeout = TimeSpan.FromMinutes(3);
        }).Services.BuildServiceProvider();
        var embedding = new OllamaEmbeddingService(services.GetRequiredService<IHttpClientFactory>(),
            Options.Create(ollama), NullLogger<OllamaEmbeddingService>.Instance);

        // Resolve the installed model before creating a database. No implicit pull/download.
        using var http = new HttpClient { BaseAddress = new Uri(ollama.BaseUrl), Timeout = TimeSpan.FromSeconds(15) };
        string tagsJson;
        try
        {
            tagsJson = await http.GetStringAsync("/api/tags", ct);
        }
        catch (TaskCanceledException exception) when (!ct.IsCancellationRequested)
        {
            // HttpClient reports its own timeout as a cancellation; Ctrl+C and the run deadline keep their own message.
            throw new InvalidOperationException($"Ollama at {ollama.BaseUrl} did not respond within 15 seconds.", exception);
        }
        using JsonDocument tags = JsonDocument.Parse(tagsJson);
        string modelName = ollama.EmbeddingModel.Contains(':') ? ollama.EmbeddingModel : ollama.EmbeddingModel + ":latest";
        string? modelDigest = tags.RootElement.GetProperty("models").EnumerateArray()
            .Where(model => model.GetProperty("name").GetString() == modelName)
            .Select(model => model.GetProperty("digest").GetString()).SingleOrDefault();
        if (modelDigest is null)
            throw new InvalidOperationException("The requested embedding model is not installed.");

        Console.WriteLine($"Dataset: {dataset.Name}; {dataset.Documents.Count} documents, {dataset.Queries.Count} judged queries.");
        Console.WriteLine($"Creating disposable database {databaseName}.");
        await using var admin = new NpgsqlConnection(adminSettings.ConnectionString);
        await admin.OpenAsync(ct);
        await EvaluationDatabaseLifecycle.RunAsync(async token =>
        {
            // Identifier is generated entirely here from a constant and a Guid, never user input.
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", admin);
            await create.ExecuteNonQueryAsync(token);
        }, async token =>
        {
            var dbOptions = new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql(evaluationSettings.ConnectionString, postgres => postgres.UseVector()).Options;
            await using var db = new AppDbContext(dbOptions);
            await db.Database.MigrateAsync(ct);
            var sourceBuilder = new NpgsqlDataSourceBuilder(evaluationSettings.ConnectionString);
            sourceBuilder.UseVector();
            await using NpgsqlDataSource source = sourceBuilder.Build();
            var tenant = new Tenant { TenantName = "Phase 4 evaluation", TenantDescription = "Disposable synthetic corpus", Email = "evaluation@example.test" };
            db.Tenants.Add(tenant);
            await db.SaveChangesAsync(ct);
            var ingestion = new DocumentIngestionService(db,
                new ChunkingService(Options.Create(chunkingSettings), NullLogger<ChunkingService>.Instance),
                embedding, NullLogger<DocumentIngestionService>.Instance);
            var documentIds = new Dictionary<Guid, string>();
            foreach (RetrievalEvaluationDocument document in dataset.Documents)
            {
                DocumentIngestionResult result = await ingestion.IngestAsync(new DocumentIngestionRequest(
                    tenant.TenantId, document.Title, null, document.Content), ct);
                documentIds.Add(result.DocumentId, document.Id);
                Console.WriteLine($"Ingested {document.Id}: {result.ChunkCount} chunk(s).");
            }
            await db.Database.ExecuteSqlRawAsync("ANALYZE chunks", ct);

            string[] questions = dataset.Queries.Select(q => q.Query.Trim())
                .Concat(dataset.UnanswerableQueries.Select(q => q.Query.Trim())).Distinct(StringComparer.Ordinal).ToArray();
            Console.WriteLine("Embedding evaluation queries once for identical vectors in both modes.");
            var embedTimer = Stopwatch.StartNew();
            IReadOnlyList<float[]> vectors = await embedding.EmbedBatchAsync(questions, ct);
            embedTimer.Stop();
            var fixedEmbedding = new EvaluationEmbeddings(questions.Zip(vectors).ToDictionary(pair => pair.First, pair => pair.Second, StringComparer.Ordinal));
            var search = new SemanticSearchService(db, fixedEmbedding,
                new PgVectorStoreService(source, NullLogger<PgVectorStoreService>.Instance),
                Options.Create(searchSettings), NullLogger<SemanticSearchService>.Instance,
                new PgFullTextSearchService(source));

            // Warm both code paths before timing; alternate first mode to reduce ordering bias.
            foreach (string mode in new[] { "vector", "hybrid" })
                await search.SearchAsync(new(tenant.TenantId, questions[0], options.TopK, options.MinSimilarity, mode), ct);
            var rows = new List<QueryRun>();
            int index = 0;
            foreach (RetrievalEvaluationQuery query in dataset.Queries)
            {
                foreach (string mode in index++ % 2 == 0 ? new[] { "vector", "hybrid" } : new[] { "hybrid", "vector" })
                {
                    var timer = Stopwatch.StartNew();
                    SemanticSearchResponse response = await search.SearchAsync(new(tenant.TenantId, query.Query, options.TopK, options.MinSimilarity, mode), ct);
                    timer.Stop();
                    var hits = response.Matches.Select(match => new Hit(documentIds[match.DocumentId], match.ChunkIndex, match.Score, match.RankingScore)).ToArray();
                    rows.Add(new QueryRun(query.Id, query.Category, query.Query, mode, query.RelevantDocumentIds,
                        RetrievalMetrics.Calculate(hits.Select(hit => hit.DocumentId).ToArray(), query.RelevantDocumentIds, options.TopK),
                        timer.Elapsed.TotalMilliseconds, hits));
                }
                Console.WriteLine($"Evaluated {query.Id}.");
            }
            var probes = new List<object>();
            foreach (RetrievalEvaluationProbe probe in dataset.UnanswerableQueries)
            {
                foreach (string mode in new[] { "vector", "hybrid" })
                {
                    var response = await search.SearchAsync(new(tenant.TenantId, probe.Query, options.TopK, options.MinSimilarity, mode), ct);
                    probes.Add(new { probe.Id, probe.Query, mode, returnedDocuments = response.Matches.Select(m => documentIds[m.DocumentId]).Distinct().ToArray(), note = "No relevance judgments; nonempty retrieval is not evidence that the question is answerable." });
                }
            }
            var summaries = rows.GroupBy(row => row.Mode).ToDictionary(group => group.Key, group => new
            {
                recall = group.Average(row => row.Metrics.Recall),
                mrr = group.Average(row => row.Metrics.ReciprocalRank),
                ndcg = group.Average(row => row.Metrics.Ndcg),
                meanRetrievalMs = group.Average(row => row.RetrievalMs)
            });
            string corpusSha256 = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(dataset, HashJsonOptions))).ToLowerInvariant();
            var report = new
            {
                schemaVersion = 1, createdAtUtc = DateTimeOffset.UtcNow, dataset = dataset.Name, corpusSha256,
                model = modelName, modelDigest, options.TopK, options.MinSimilarity, searchSettings.CandidateLimit, searchSettings.RrfConstant,
                chunkingSettings, chunkCount = await db.Chunks.CountAsync(ct), queryEmbeddingMs = embedTimer.Elapsed.TotalMilliseconds,
                timing = "Warmed retrieval service only, query embeddings precomputed and identical across modes. One observation per query/mode; not a performance benchmark.",
                evaluation = "Binary document judgments. Deduplicate returned chunks to document IDs, retain rank order. Retrieval returns TopK chunks, so fewer than K unique documents may be available.",
                summaries, queries = rows, unanswerableProbes = probes
            };
            Directory.CreateDirectory(options.OutputDirectory);
            string stem = "retrieval-" + DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffZ", CultureInfo.InvariantCulture);
            string jsonPath = Path.GetFullPath(Path.Combine(options.OutputDirectory, stem + ".json"));
            await File.WriteAllTextAsync(jsonPath, JsonSerializer.Serialize(report, JsonOptions), ct);
            var markdown = new StringBuilder($"# Phase 4 retrieval evaluation\n\nSynthetic corpus: {dataset.Name}. Corpus SHA-256: `{corpusSha256}`.\n\nModel: `{modelName}` (`{modelDigest}`). TopK={options.TopK}, MinSimilarity={options.MinSimilarity.ToString(CultureInfo.InvariantCulture)}.\n\n| Mode | Recall@K | MRR@K | NDCG@K | Mean retrieval ms |\n|---|---:|---:|---:|---:|\n");
            foreach (var (mode, summary) in summaries.OrderBy(pair => pair.Key))
                markdown.AppendLine(FormattableString.Invariant($"| {mode} | {summary.recall:F4} | {summary.mrr:F4} | {summary.ndcg:F4} | {summary.meanRetrievalMs:F2} |"));
            markdown.AppendLine("\nResults apply only to this authored corpus. Latency excludes embedding and generation and has one warmed observation per mode/query. No reranker was used. Unanswerable probes are reported separately in JSON.");
            markdown.AppendLine("\n## Query changes\n\n| Query | Recall delta | Reciprocal rank delta | NDCG delta |\n|---|---:|---:|---:|");
            foreach (var pair in rows.GroupBy(row => row.Id))
            {
                var baseline = pair.Single(row => row.Mode == "vector").Metrics;
                var hybrid = pair.Single(row => row.Mode == "hybrid").Metrics;
                markdown.AppendLine(FormattableString.Invariant($"| {pair.Key} | {hybrid.Recall - baseline.Recall:+0.0000;-0.0000;0.0000} | {hybrid.ReciprocalRank - baseline.ReciprocalRank:+0.0000;-0.0000;0.0000} | {hybrid.Ndcg - baseline.Ndcg:+0.0000;-0.0000;0.0000} |"));
            }
            await File.WriteAllTextAsync(Path.ChangeExtension(jsonPath, ".md"), markdown.ToString(), ct);
            Console.WriteLine(markdown.ToString());
            Console.WriteLine($"Report: {jsonPath}");
        }, async cleanupToken =>
        {
            await using var cleanupConnection = new NpgsqlConnection(adminSettings.ConnectionString);
            await cleanupConnection.OpenAsync(cleanupToken);
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)", cleanupConnection);
            await drop.ExecuteNonQueryAsync(cleanupToken);
            Console.WriteLine($"Removed disposable database {databaseName}.");
        }, failure => Console.Error.WriteLine($"Cleanup failed ({failure.GetType().Name}). Disposable database {databaseName} may remain; remove that exact database after checking active connections."), ct);
    }

    private sealed record Hit(string DocumentId, int ChunkIndex, double Score, double? RankingScore);
    private sealed record QueryRun(string Id, string Category, string Query, string Mode, IReadOnlyList<string> RelevantDocumentIds,
        RetrievalMetricResult Metrics, double RetrievalMs, IReadOnlyList<Hit> Hits);

    private sealed class EvaluationEmbeddings(IReadOnlyDictionary<string, float[]> vectors) : IEmbeddingService
    {
        public Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(vectors[text.Trim()]);
        }

        public Task<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<float[]>>(texts.Select(text => vectors[text]).ToArray());
        }
    }
}
