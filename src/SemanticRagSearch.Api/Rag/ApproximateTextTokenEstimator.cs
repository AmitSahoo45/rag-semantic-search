using Microsoft.Extensions.Options;
using SemanticRagSearch.Api.Configuration;

namespace SemanticRagSearch.Api.Rag;

public sealed class ApproximateTextTokenEstimator : ITextTokenEstimator
{
    private readonly double _estimatedCharactersPerToken;

    public ApproximateTextTokenEstimator(IOptions<RagSettings> settings)
    {
        RagSettings ragSettings =
            settings?.Value ?? throw new ArgumentNullException(nameof(settings));

        _estimatedCharactersPerToken = ragSettings.EstimatedCharactersPerToken;

        if (_estimatedCharactersPerToken <= 0 ||
            double.IsNaN(_estimatedCharactersPerToken) ||
            double.IsInfinity(_estimatedCharactersPerToken))
        {
            throw new InvalidOperationException(
                "EstimatedCharactersPerToken must be a finite value greater than zero.");
        }
    }

    public int EstimateTokens(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (text.Length == 0)
            return 0;

        return (int)Math.Ceiling(text.Length / _estimatedCharactersPerToken);
    }
}
