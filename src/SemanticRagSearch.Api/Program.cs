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
using SemanticRagSearch.Api.Authentication;
using SemanticRagSearch.Api.Observability;
using SemanticRagSearch.Api.RateLimiting;
using SemanticRagSearch.Api.Resilience;
using Microsoft.AspNetCore.Authentication;
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
builder.Services.AddControllers(options => options.Filters.Add<TenantScopeFilter>());
builder.Services.AddAuthentication(ApiKeyCredentials.Scheme)
    .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyCredentials.Scheme, _ => { });
builder.Services.AddAuthorization();
builder.Services.AddScoped<IApiKeyTenantStore, ApiKeyTenantStore>();
builder.Services.AddTenantRateLimiting(builder.Configuration);
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();

builder.Services.AddHttpClient("Ollama", client =>
{
    OllamaSettings OllamaConfig = builder.Configuration.GetSection(OllamaSettings.SectionName).Get<OllamaSettings>() ?? new OllamaSettings();
    client.BaseAddress = new Uri(OllamaConfig.BaseUrl);
}).AddOllamaResilience(builder.Configuration);
builder.Services.AddSingleton<NpgsqlDataSource>(_ =>
{
    var dataSourceBuilder = new NpgsqlDataSourceBuilder(builder.Configuration.GetConnectionString("DefaultConnection"));
    dataSourceBuilder.UseVector();
    return dataSourceBuilder.Build();
});
builder.Services.AddScoped<IChunkingService, ChunkingService>();
builder.Services.AddScoped<OllamaEmbeddingService>();
builder.Services.AddScoped<IVectorStoreService, PgVectorStoreService>();
builder.Services.AddScoped<IDocumentIngestionService, DocumentIngestionService>();
builder.Services.AddScoped<SemanticSearchService>();
builder.Services.AddScoped<OllamaTextGenerationService>();
builder.Services.AddScoped<ITextTokenEstimator, ApproximateTextTokenEstimator>();
builder.Services.AddScoped<IContextAssembler, ContextAssembler>();
builder.Services.AddScoped<RagService>();
builder.Services.AddRagObservability(builder.Configuration);

builder.Services.AddHealthChecks().AddNpgSql(builder.Configuration.GetConnectionString("DefaultConnection")!);
builder.Services.AddDbContext<AppDbContext>(options => options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection"), npgsqlOptions => npgsqlOptions.UseVector()));

var app = builder.Build();

if (await ApiKeyProvisioningCommand.TryRunAsync(args, app.Services))
{
    await app.DisposeAsync();
    return;
}

app.UseExceptionHandler(static _ => { });
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<TenantRateLimitMiddleware>();
app.MapControllers().RequireAuthorization();
app.MapHealthChecks("/health").AllowAnonymous();
app.MapGet("/", () => "Semantic RAG Search API is running").AllowAnonymous();

app.Run();

public partial class Program
{
}
