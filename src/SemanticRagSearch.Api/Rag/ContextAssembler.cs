using System.Globalization;
using System.Text;
using SemanticRagSearch.Api.Search;

namespace SemanticRagSearch.Api.Rag;

public sealed class ContextAssembler : IContextAssembler
{
    private static readonly string SourceSeparator =
        $"{Environment.NewLine}{Environment.NewLine}---{Environment.NewLine}{Environment.NewLine}";

    private readonly ITextTokenEstimator _tokenEstimator;
    private readonly ILogger<ContextAssembler> _logger;

    public ContextAssembler(
        ITextTokenEstimator tokenEstimator,
        ILogger<ContextAssembler> logger)
    {
        _tokenEstimator =
            tokenEstimator ?? throw new ArgumentNullException(nameof(tokenEstimator));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public ContextAssemblyResult Assemble(
        IReadOnlyList<SemanticSearchMatch> matches,
        int maxTokens)
    {
        ArgumentNullException.ThrowIfNull(matches);

        if (maxTokens < 0)
            throw new ArgumentOutOfRangeException(nameof(maxTokens), "Token budget cannot be negative.");

        if (maxTokens == 0)
        {
            bool hasUsableSource = matches.Any(
                match => !string.IsNullOrWhiteSpace(match.TextContent));

            return new ContextAssemblyResult("", [], 0, 0, hasUsableSource);
        }

        var contextBuilder = new StringBuilder();
        var sources = new List<RagSource>();
        bool wasTruncated = false;

        foreach (SemanticSearchMatch match in matches)
        {
            string textContent = match.TextContent.Trim();

            if (textContent.Length == 0)
                continue;

            RagSource source = CreateSource(match, sources.Count + 1, textContent);
            string formattedSource = FormatSource(source);
            string candidateContext = BuildCandidateContext(contextBuilder, formattedSource);

            if (_tokenEstimator.EstimateTokens(candidateContext) <= maxTokens)
            {
                AppendSource(contextBuilder, formattedSource);
                sources.Add(source);
                continue;
            }

            string? fittingPrefix = FindLongestFittingPrefix(
                contextBuilder,
                source,
                maxTokens);

            if (fittingPrefix is not null)
            {
                RagSource truncatedSource = source with { TextContent = fittingPrefix };
                AppendSource(contextBuilder, FormatSource(truncatedSource));
                sources.Add(truncatedSource);
            }

            wasTruncated = true;
            break;
        }

        string context = contextBuilder.ToString();
        int estimatedTokens = _tokenEstimator.EstimateTokens(context);

        _logger.LogInformation(
            "Assembled RAG context with {SourceCount} source(s), approximately {EstimatedTokens} " +
            "token(s) of {TokenBudget}; truncated={WasTruncated}.",
            sources.Count,
            estimatedTokens,
            maxTokens,
            wasTruncated);

        return new ContextAssemblyResult(
            context,
            sources,
            estimatedTokens,
            maxTokens,
            wasTruncated);
    }

    private string? FindLongestFittingPrefix(
        StringBuilder currentContext,
        RagSource source,
        int maxTokens)
    {
        int low = 1;
        int high = source.TextContent.Length - 1;
        string? bestPrefix = null;

        while (low <= high)
        {
            int candidateLength = low + ((high - low) / 2);
            string prefix = source.TextContent[..candidateLength].TrimEnd();

            if (prefix.Length == 0)
            {
                low = candidateLength + 1;
                continue;
            }

            prefix = AvoidTrailingHighSurrogate(prefix);
            if (prefix.Length == 0)
            {
                low = candidateLength + 1;
                continue;
            }

            RagSource candidateSource = source with { TextContent = prefix };
            string candidateContext = BuildCandidateContext(
                currentContext,
                FormatSource(candidateSource));

            if (_tokenEstimator.EstimateTokens(candidateContext) <= maxTokens)
            {
                bestPrefix = prefix;
                low = candidateLength + 1;
            }
            else
            {
                high = candidateLength - 1;
            }
        }

        return bestPrefix;
    }

    private static RagSource CreateSource(
        SemanticSearchMatch match,
        int sourceNumber,
        string textContent)
    {
        return new RagSource(
            sourceNumber,
            match.ChunkId,
            match.DocumentId,
            match.DocumentTitle,
            match.SourceUrl,
            match.ChunkIndex,
            match.Score,
            textContent);
    }

    private static string FormatSource(RagSource source)
    {
        var lines = new List<string>
        {
            $"[{source.SourceNumber}] Document: {source.DocumentTitle}",
            $"ChunkIndex: {source.ChunkIndex}",
            $"Score: {source.Score.ToString("0.0000", CultureInfo.InvariantCulture)}"
        };

        if (!string.IsNullOrWhiteSpace(source.SourceUrl))
            lines.Add($"SourceUrl: {source.SourceUrl}");

        lines.Add(source.TextContent);
        return string.Join(Environment.NewLine, lines);
    }

    private static string BuildCandidateContext(
        StringBuilder currentContext,
        string formattedSource)
    {
        return currentContext.Length == 0
            ? formattedSource
            : string.Concat(currentContext.ToString(), SourceSeparator, formattedSource);
    }

    private static void AppendSource(
        StringBuilder contextBuilder,
        string formattedSource)
    {
        if (contextBuilder.Length > 0)
            contextBuilder.Append(SourceSeparator);

        contextBuilder.Append(formattedSource);
    }

    private static string AvoidTrailingHighSurrogate(string value)
    {
        if (value.Length > 0 && char.IsHighSurrogate(value[^1]))
            return value[..^1];

        return value;
    }
}
