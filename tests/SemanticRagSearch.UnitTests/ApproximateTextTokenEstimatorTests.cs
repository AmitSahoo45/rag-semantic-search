using Microsoft.Extensions.Options;
using SemanticRagSearch.Api.Configuration;
using SemanticRagSearch.Api.Rag;

namespace SemanticRagSearch.UnitTests;

public class ApproximateTextTokenEstimatorTests
{
    [Theory]
    [InlineData("", 0)]
    [InlineData("a", 1)]
    [InlineData("abcd", 1)]
    [InlineData("abcde", 2)]
    public void EstimateTokens_RoundsUpUsingConfiguredRatio(
        string text,
        int expectedTokens)
    {
        ApproximateTextTokenEstimator estimator = CreateEstimator(4.0);

        int tokenCount = estimator.EstimateTokens(text);

        Assert.Equal(expectedTokens, tokenCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Constructor_RejectsInvalidCharactersPerToken(double value)
    {
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => CreateEstimator(value));

        Assert.Contains("EstimatedCharactersPerToken", exception.Message);
    }

    private static ApproximateTextTokenEstimator CreateEstimator(
        double estimatedCharactersPerToken)
    {
        return new ApproximateTextTokenEstimator(
            Options.Create(new RagSettings
            {
                EstimatedCharactersPerToken = estimatedCharactersPerToken
            }));
    }
}
