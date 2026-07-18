using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using SemanticRagSearch.Api.Rag;
using SemanticRagSearch.Api.Search;

namespace SemanticRagSearch.UnitTests;

public class ContextAssemblerTests
{
    [Fact]
    public void Assemble_FormatsSourcesInRelevanceOrder()
    {
        SemanticSearchMatch first = CreateMatch(
            title: "First",
            sourceUrl: "https://example.test/first",
            chunkIndex: 2,
            content: "first evidence",
            score: 0.9);
        SemanticSearchMatch second = CreateMatch(
            title: "Second",
            sourceUrl: null,
            chunkIndex: 4,
            content: "second evidence",
            score: 0.8);
        ContextAssembler assembler = CreateAssembler();

        ContextAssemblyResult result = assembler.Assemble([first, second], maxTokens: 1_000);

        string expectedContext = string.Join(
            $"{Environment.NewLine}{Environment.NewLine}---{Environment.NewLine}{Environment.NewLine}",
            FormatExpectedSource(1, first, "first evidence"),
            FormatExpectedSource(2, second, "second evidence"));

        Assert.Equal(expectedContext, result.Context);
        Assert.Equal(["first evidence", "second evidence"], result.Sources.Select(source => source.TextContent));
        Assert.Equal([1, 2], result.Sources.Select(source => source.SourceNumber));
        Assert.Equal(expectedContext.Length, result.EstimatedTokenCount);
        Assert.Equal(1_000, result.TokenBudget);
        Assert.False(result.WasTruncated);
    }

    [Fact]
    public void Assemble_StopsWhenNextSourceCannotFit()
    {
        SemanticSearchMatch first = CreateMatch(title: "First", content: "first evidence");
        SemanticSearchMatch second = CreateMatch(title: "Second", content: "second evidence");
        ContextAssembler assembler = CreateAssembler();
        string firstContext = FormatExpectedSource(1, first, "first evidence");

        ContextAssemblyResult result = assembler.Assemble(
            [first, second],
            maxTokens: firstContext.Length);

        RagSource includedSource = Assert.Single(result.Sources);
        Assert.Equal("first evidence", includedSource.TextContent);
        Assert.Equal(firstContext, result.Context);
        Assert.True(result.WasTruncated);
    }

    [Fact]
    public void Assemble_TruncatesOversizedFirstSourceToLongestFittingPrefix()
    {
        SemanticSearchMatch match = CreateMatch(
            title: "Large",
            content: "abcdefghijklmnopqrstuvwxyz");
        ContextAssembler assembler = CreateAssembler();
        string expectedContent = "abcdefghij";
        string expectedContext = FormatExpectedSource(1, match, expectedContent);

        ContextAssemblyResult result = assembler.Assemble(
            [match],
            maxTokens: expectedContext.Length);

        RagSource source = Assert.Single(result.Sources);
        Assert.Equal(expectedContent, source.TextContent);
        Assert.Equal(expectedContext, result.Context);
        Assert.Equal(expectedContext.Length, result.EstimatedTokenCount);
        Assert.True(result.WasTruncated);
    }

    [Fact]
    public void Assemble_DoesNotEmitEmptySourceWhenUnicodeContentCannotFit()
    {
        SemanticSearchMatch match = CreateMatch(
            title: "Unicode",
            content: "😀");
        ContextAssembler assembler = CreateAssembler();
        string headerOnlyContext = FormatExpectedSource(1, match, "");

        ContextAssemblyResult result = assembler.Assemble(
            [match],
            maxTokens: headerOnlyContext.Length);

        Assert.Empty(result.Sources);
        Assert.Empty(result.Context);
        Assert.Equal(0, result.EstimatedTokenCount);
        Assert.True(result.WasTruncated);
    }

    [Fact]
    public void Assemble_SkipsBlankSourceContent()
    {
        ContextAssembler assembler = CreateAssembler();

        ContextAssemblyResult result = assembler.Assemble(
            [
                CreateMatch(title: "Empty", content: ""),
                CreateMatch(title: "Whitespace", content: "   ")
            ],
            maxTokens: 100);

        Assert.Empty(result.Sources);
        Assert.Empty(result.Context);
        Assert.Equal(0, result.EstimatedTokenCount);
        Assert.False(result.WasTruncated);
    }

    [Fact]
    public void Assemble_ZeroBudgetReturnsEmptyTruncatedResult()
    {
        ContextAssembler assembler = CreateAssembler();

        ContextAssemblyResult result = assembler.Assemble(
            [CreateMatch(title: "First", content: "evidence")],
            maxTokens: 0);

        Assert.Empty(result.Sources);
        Assert.Empty(result.Context);
        Assert.Equal(0, result.EstimatedTokenCount);
        Assert.Equal(0, result.TokenBudget);
        Assert.True(result.WasTruncated);
    }

    [Fact]
    public void Assemble_RejectsNullMatches()
    {
        ContextAssembler assembler = CreateAssembler();

        Assert.Throws<ArgumentNullException>(
            () => assembler.Assemble(null!, maxTokens: 100));
    }

    [Fact]
    public void Assemble_RejectsNegativeBudget()
    {
        ContextAssembler assembler = CreateAssembler();

        ArgumentOutOfRangeException exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => assembler.Assemble([], maxTokens: -1));

        Assert.Equal("maxTokens", exception.ParamName);
    }

    private static ContextAssembler CreateAssembler()
    {
        return new ContextAssembler(
            new CharacterCountingTokenEstimator(),
            NullLogger<ContextAssembler>.Instance);
    }

    private static SemanticSearchMatch CreateMatch(
        string title,
        string content,
        string? sourceUrl = null,
        int chunkIndex = 0,
        double score = 0.9)
    {
        return new SemanticSearchMatch(
            Guid.NewGuid(),
            Guid.NewGuid(),
            title,
            sourceUrl,
            chunkIndex,
            content,
            score);
    }

    private static string FormatExpectedSource(
        int sourceNumber,
        SemanticSearchMatch match,
        string content)
    {
        var lines = new List<string>
        {
            $"[{sourceNumber}] Document: {match.DocumentTitle}",
            $"ChunkIndex: {match.ChunkIndex}",
            $"Score: {match.Score.ToString("0.0000", CultureInfo.InvariantCulture)}"
        };

        if (!string.IsNullOrWhiteSpace(match.SourceUrl))
            lines.Add($"SourceUrl: {match.SourceUrl}");

        lines.Add(content);
        return string.Join(Environment.NewLine, lines);
    }

    private sealed class CharacterCountingTokenEstimator : ITextTokenEstimator
    {
        public int EstimateTokens(string text) => text.Length;
    }
}
