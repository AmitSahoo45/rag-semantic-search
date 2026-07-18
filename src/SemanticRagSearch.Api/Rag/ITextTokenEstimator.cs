namespace SemanticRagSearch.Api.Rag;

public interface ITextTokenEstimator
{
    int EstimateTokens(string text);
}
