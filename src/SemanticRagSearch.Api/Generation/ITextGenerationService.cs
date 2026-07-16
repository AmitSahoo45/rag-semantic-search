namespace SemanticRagSearch.Api.Generation;

public interface ITextGenerationService
{
    Task<string> GenerateAsync(
        string systemPrompt,
        string userPrompt,
        CancellationToken cancellationToken = default);
}
