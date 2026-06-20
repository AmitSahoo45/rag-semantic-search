using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using SemanticRagSearch.Api.Configuration;
using SemanticRagSearch.Api.Domain;

namespace SemanticRagSearch.Api.Chunking;

public class ChunkingService : IChunkingService
{
    private static readonly Regex ParagraphSplitRegex = new(@"\n\s*\n", RegexOptions.Compiled);
    private static readonly Regex SentenceSplitRegex = new(@"(?<=[.!?])\s+", RegexOptions.Compiled);
    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);

    private readonly ChunkingSettings _settings;
    private readonly ILogger<ChunkingService> _logger;

    public ChunkingService(IOptions<ChunkingSettings> settings, ILogger<ChunkingService> logger)
    {
        _settings = settings?.Value ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        if (_settings.ChunkSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(settings), "ChunkSize must be greater than zero.");

        if (_settings.ChunkOverlap < 0)
            throw new ArgumentOutOfRangeException(nameof(settings), "ChunkOverlap cannot be negative.");

        if (_settings.ChunkOverlap >= _settings.ChunkSize)
            throw new ArgumentOutOfRangeException(nameof(settings), "ChunkOverlap must be smaller than ChunkSize.");
    }

    public IReadOnlyList<Chunk> ChunkDocument(Document document, string content)
    {
        if (document is null)
            throw new ArgumentNullException(nameof(document));

        if (document.DocumentId == Guid.Empty)
            throw new ArgumentException("DocumentId cannot be empty.", nameof(document));

        if (document.TenantId == Guid.Empty)
            throw new ArgumentException("TenantId cannot be empty.", nameof(document));

        if (string.IsNullOrWhiteSpace(content))
            throw new ArgumentException("Document content cannot be empty.", nameof(content));

        List<TextUnit> units = SplitIntoTextUnits(content);
        List<ChunkSegment> segments = PackUnitsIntoChunks(units);

        List<Chunk> chunks = segments
            .Select((segment, index) => new Chunk
            {
                DocumentId = document.DocumentId,
                TenantId = document.TenantId,
                ChunkIndex = index,
                TextContent = segment.Text,
                TokenCount = EstimateTokenCount(segment.Text),
                MetaData = new ChunkMetaData
                {
                    ChunkStrategy = segment.Strategy
                }
            })
            .ToList();

        _logger.LogInformation(
            "Split document {DocumentId} into {ChunkCount} chunk(s).",
            document.DocumentId,
            chunks.Count);

        return chunks;
    }

    private List<TextUnit> SplitIntoTextUnits(string content)
    {
        string normalizedContent = NormalizeNewlines(content).Trim();
        string[] paragraphs = ParagraphSplitRegex.Split(normalizedContent);
        var units = new List<TextUnit>();

        foreach (string rawParagraph in paragraphs)
        {
            string paragraph = NormalizeWhitespace(rawParagraph);

            if (paragraph.Length == 0)
                continue;

            if (EstimateTokenCount(paragraph) <= _settings.ChunkSize)
            {
                units.Add(new TextUnit(paragraph, "paragraph"));
                continue;
            }

            units.AddRange(SplitOversizedParagraph(paragraph));
        }

        return units;
    }

    private IEnumerable<TextUnit> SplitOversizedParagraph(string paragraph)
    {
        string[] sentences = SentenceSplitRegex.Split(paragraph);

        foreach (string rawSentence in sentences)
        {
            string sentence = NormalizeWhitespace(rawSentence);

            if (sentence.Length == 0)
                continue;

            if (EstimateTokenCount(sentence) <= _settings.ChunkSize)
            {
                yield return new TextUnit(sentence, "sentence");
                continue;
            }

            foreach (TextUnit split in HardSplit(sentence))
            {
                yield return split;
            }
        }
    }

    private IEnumerable<TextUnit> HardSplit(string text)
    {
        int maxCharacters = MaxCharactersPerChunk();
        string[] words = WhitespaceRegex.Split(text);
        var currentWords = new List<string>();
        int currentLength = 0;

        foreach (string word in words)
        {
            if (word.Length == 0)
                continue;

            if (word.Length > maxCharacters)
            {
                if (currentWords.Count > 0)
                {
                    yield return new TextUnit(string.Join(' ', currentWords), "hard_split");
                    currentWords.Clear();
                    currentLength = 0;
                }

                for (int start = 0; start < word.Length; start += maxCharacters)
                {
                    int length = Math.Min(maxCharacters, word.Length - start);
                    yield return new TextUnit(word.Substring(start, length), "hard_split");
                }

                continue;
            }

            int candidateLength = currentWords.Count == 0
                ? word.Length
                : currentLength + 1 + word.Length;

            if (candidateLength > maxCharacters && currentWords.Count > 0)
            {
                yield return new TextUnit(string.Join(' ', currentWords), "hard_split");
                currentWords.Clear();
                currentLength = 0;
            }

            currentWords.Add(word);
            currentLength = currentWords.Count == 1
                ? word.Length
                : currentLength + 1 + word.Length;
        }

        if (currentWords.Count > 0)
            yield return new TextUnit(string.Join(' ', currentWords), "hard_split");
    }

    private List<ChunkSegment> PackUnitsIntoChunks(IReadOnlyList<TextUnit> units)
    {
        var chunks = new List<ChunkSegment>();
        string currentText = string.Empty;
        string currentStrategy = "paragraph";

        foreach (TextUnit unit in units)
        {
            if (currentText.Length == 0)
            {
                currentText = unit.Text;
                currentStrategy = unit.Strategy;
                continue;
            }

            string candidate = JoinText(currentText, unit.Text);

            if (EstimateTokenCount(candidate) <= _settings.ChunkSize)
            {
                currentText = candidate;
                currentStrategy = MergeStrategy(currentStrategy, unit.Strategy);
                continue;
            }

            chunks.Add(new ChunkSegment(currentText, currentStrategy));

            string overlapText = GetOverlapText(currentText);
            string nextText = overlapText.Length == 0 ? unit.Text : JoinText(overlapText, unit.Text);

            if (EstimateTokenCount(nextText) <= _settings.ChunkSize)
            {
                currentText = nextText;
                currentStrategy = MergeStrategy(currentStrategy, unit.Strategy);
            }
            else
            {
                currentText = unit.Text;
                currentStrategy = unit.Strategy;
            }
        }

        if (currentText.Length > 0)
            chunks.Add(new ChunkSegment(currentText, currentStrategy));

        return chunks;
    }

    private string GetOverlapText(string text)
    {
        if (_settings.ChunkOverlap == 0)
            return string.Empty;

        int maxCharacters = _settings.ChunkOverlap * 4;

        if (text.Length <= maxCharacters)
            return text;

        string[] words = WhitespaceRegex.Split(text);
        var selectedWords = new Stack<string>();
        int selectedLength = 0;

        for (int i = words.Length - 1; i >= 0; i--)
        {
            string word = words[i];

            if (word.Length == 0)
                continue;

            int candidateLength = selectedWords.Count == 0
                ? word.Length
                : selectedLength + 1 + word.Length;

            if (candidateLength > maxCharacters)
                break;

            selectedWords.Push(word);
            selectedLength = candidateLength;
        }

        return string.Join(' ', selectedWords);
    }

    private int EstimateTokenCount(string text)
    {
        return Math.Max(1, (int)Math.Ceiling(text.Length / 4.0));
    }

    private int MaxCharactersPerChunk()
    {
        return _settings.ChunkSize * 4;
    }

    private static string NormalizeNewlines(string text)
    {
        return text.Replace("\r\n", "\n").Replace('\r', '\n');
    }

    private static string NormalizeWhitespace(string text)
    {
        return WhitespaceRegex.Replace(text.Trim(), " ");
    }

    private static string JoinText(string first, string second)
    {
        return $"{first}\n\n{second}";
    }

    private static string MergeStrategy(string currentStrategy, string nextStrategy)
    {
        if (currentStrategy == "hard_split" || nextStrategy == "hard_split")
            return "hard_split";

        if (currentStrategy == "sentence" || nextStrategy == "sentence")
            return "sentence";

        return "paragraph";
    }

    private sealed record TextUnit(string Text, string Strategy);

    private sealed record ChunkSegment(string Text, string Strategy);
}
