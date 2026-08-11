using SemanticRagSearch.Api.Configuration;
using SemanticRagSearch.Api.Chunking;
using SemanticRagSearch.Api.Data;
using SemanticRagSearch.Api.Embedding;
using SemanticRagSearch.Api.Errors;
using SemanticRagSearch.Api.Generation;
using SemanticRagSearch.Api.Ingestion;
using SemanticRagSearch.Api.Rag;
using SemanticRagSearch.Api.Search;
using SemanticRagSearch.Api.VectorStore;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Pgvector;
using Pgvector.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<OllamaSettings>(builder.Configuration.GetSection(OllamaSettings.SectionName));
builder.Services.Configure<ChunkingSettings>(builder.Configuration.GetSection(ChunkingSettings.SectionName));
builder.Services.Configure<SearchSettings>(builder.Configuration.GetSection(SearchSettings.SectionName));
builder.Services.Configure<RagSettings>(builder.Configuration.GetSection(RagSettings.SectionName));

// -----------
// Add services
builder.Services.AddControllers();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();

builder.Services.AddHttpClient("Ollama", client =>
{
    OllamaSettings OllamaConfig = builder.Configuration.GetSection(OllamaSettings.SectionName).Get<OllamaSettings>() ?? new OllamaSettings();
    client.BaseAddress = new Uri(OllamaConfig.BaseUrl);
    client.Timeout = TimeSpan.FromSeconds(120);
});
builder.Services.AddSingleton<NpgsqlDataSource>(_ =>
{
    var dataSourceBuilder = new NpgsqlDataSourceBuilder(builder.Configuration.GetConnectionString("DefaultConnection"));
    dataSourceBuilder.UseVector();
    return dataSourceBuilder.Build();
});
builder.Services.AddScoped<IChunkingService, ChunkingService>();
builder.Services.AddScoped<IEmbeddingService, OllamaEmbeddingService>();
builder.Services.AddScoped<IVectorStoreService, PgVectorStoreService>();
builder.Services.AddScoped<IDocumentIngestionService, DocumentIngestionService>();
builder.Services.AddScoped<ISemanticSearchService, SemanticSearchService>();
builder.Services.AddScoped<ITextGenerationService, OllamaTextGenerationService>();
builder.Services.AddScoped<ITextTokenEstimator, ApproximateTextTokenEstimator>();
builder.Services.AddScoped<IContextAssembler, ContextAssembler>();
builder.Services.AddScoped<IRagService, RagService>();

builder.Services.AddHealthChecks().AddNpgSql(builder.Configuration.GetConnectionString("DefaultConnection")!);
builder.Services.AddDbContext<AppDbContext>(options => options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection"), npgsqlOptions => npgsqlOptions.UseVector()));

var app = builder.Build();

app.UseExceptionHandler(static _ => { });
app.MapControllers();
app.MapHealthChecks("/health");
app.MapGet("/", () => "Semantic RAG Search API is running");

app.Run();

public partial class Program
{
}
