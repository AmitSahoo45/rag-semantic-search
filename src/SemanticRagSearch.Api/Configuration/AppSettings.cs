namespace SemanticRagSearch.Api.Configuration;

public class OllamaSettings // This is going to bind App:Ollama section in appsettings.json
{
    public const string SectionName = "App:Ollama";

    public string BaseUrl { get; set; } = "http://localhost:11434";
    public string EmbeddingModel { get; set; } = "nomic-embed-text";
    public int EmbeddingDimension { get; set; } = 768;
    public string ChatModel { get; set; } = "llama3.2";
}

// This will bind to App:Chunking Section 
public class ChunkingSettings
{
    public const string SectionName = "App:Chunking";

    public int ChunkSize { get; set; } = 400;
    public int ChunkOverlap { get; set; } = 100;
}


// This is gonna bind to App:Search 
public class SearchSettings
{
    public const string SectionName = "App:Search";

    public double minSimilarity { get; set; } = 0.3;
    public double defaultTopK { get; set; } = 5;
}

