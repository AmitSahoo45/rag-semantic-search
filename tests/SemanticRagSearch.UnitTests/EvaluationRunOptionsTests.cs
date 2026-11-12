using System.Globalization;
using SemanticRagSearch.Api.Evaluation;

namespace SemanticRagSearch.UnitTests;

public sealed class EvaluationRunOptionsTests
{
    [Fact]
    public void Parse_DefaultsToRepoSampleAndBoundedSearch()
    {
        var options = EvaluationRunOptions.Parse([]);
        Assert.Equal("samples/phase4/dataset.json", options.DatasetPath);
        Assert.Equal("artifacts/phase4", options.OutputDirectory);
        Assert.Equal(3, options.TopK);
        Assert.Equal(0.3, options.MinSimilarity);
    }

    [Fact]
    public void Parse_UsesInvariantNumericArguments()
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var options = EvaluationRunOptions.Parse(["--dataset", "sample.json", "--output", "out/reports", "--top-k", "5", "--min-similarity", "0.42"]);
            Assert.Equal("sample.json", options.DatasetPath);
            Assert.Equal("out/reports", options.OutputDirectory);
            Assert.Equal(5, options.TopK);
            Assert.Equal(0.42, options.MinSimilarity);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Theory]
    [InlineData("--top-k", "0")]
    [InlineData("--top-k", "21")]
    [InlineData("--min-similarity", "NaN")]
    [InlineData("--min-similarity", "Infinity")]
    [InlineData("--min-similarity", "-0.1")]
    [InlineData("--min-similarity", "1.01")]
    [InlineData("--dataset", " ")]
    [InlineData("--output", "")]
    [InlineData("--unknown", "value")]
    public void Parse_RejectsInvalidArgumentsBeforeDatabaseAccess(string name, string value) =>
        Assert.Throws<ArgumentException>(() => EvaluationRunOptions.Parse([name, value]));

    [Fact]
    public void Parse_RejectsMissingAndDuplicateOptions()
    {
        Assert.Throws<ArgumentException>(() => EvaluationRunOptions.Parse(["--output"]));
        Assert.Throws<ArgumentException>(() => EvaluationRunOptions.Parse(["--top-k", "2", "--top-k", "3"]));
    }
}
