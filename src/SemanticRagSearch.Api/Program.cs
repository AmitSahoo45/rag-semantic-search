using SemanticRagSearch.Api.Configuration;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<OllamaSettings>(builder.Configuration.GetSection(OllamaSettings.SectionName));
builder.Services.Configure<ChunkingSettings>(builder.Configuration.GetSection(ChunkingSettings.SectionName));
builder.Services.Configure<SearchSettings>(builder.Configuration.GetSection(SearchSettings.SectionName));

// -----------
// Add services
builder.Services.AddControllers();

builder.Services.AddHttpClient("Ollama", client =>
{
    OllamaSettings OllamaConfig = builder.Configuration.GetSection(OllamaSettings.SectionName).Get<OllamaSettings>() ?? new OllamaSettings();
    client.BaseAddress = new Uri(OllamaConfig.BaseUrl);
    client.Timeout = TimeSpan.FromSeconds(120);
});

builder.Services.AddHealthChecks().AddNpgSql(builder.Configuration.GetConnectionString("DefaultConnection")!);

var app = builder.Build();

app.MapControllers();
app.MapHealthChecks("/health");

app.Run();
