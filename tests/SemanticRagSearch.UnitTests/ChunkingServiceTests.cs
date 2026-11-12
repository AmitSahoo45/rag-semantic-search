using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SemanticRagSearch.Api.Chunking;
using SemanticRagSearch.Api.Configuration;
using SemanticRagSearch.Api.Domain;

namespace SemanticRagSearch.UnitTests;

public class ChunkingServiceTests
{
    private const string GrinningFace = "\U0001F600";

    [Fact]
    public void ChunkDocument_ReturnsSingleParagraphChunkForShortContent()
    {
        const string content = "The quick brown fox jumps over the lazy dog.";
        ChunkingService service = CreateChunkingService();
        Document document = CreateDocument();

        IReadOnlyList<Chunk> chunks = service.ChunkDocument(document, content);

        Chunk chunk = Assert.Single(chunks);
        Assert.Equal(content, chunk.TextContent);
        Assert.Equal(0, chunk.ChunkIndex);
        Assert.Equal(document.DocumentId, chunk.DocumentId);
        Assert.Equal(document.TenantId, chunk.TenantId);
        Assert.Equal((int)Math.Ceiling(content.Length / 4.0), chunk.TokenCount);
        Assert.Equal("paragraph", chunk.MetaData?.ChunkStrategy);
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(3, 0)]
    [InlineData(100, 0)]
    [InlineData(100, 50)]
    public void ChunkDocument_HardSplitNeverSplitsSurrogatePairs(int chunkSize, int chunkOverlap)
    {
        string content = "a" + string.Concat(Enumerable.Repeat(GrinningFace, 1000));
        ChunkingService service = CreateChunkingService(chunkSize, chunkOverlap);
        int maxCharacters = chunkSize * 4;

        IReadOnlyList<Chunk> chunks = service.ChunkDocument(CreateDocument(), content);

        Assert.NotEmpty(chunks);

        foreach (Chunk chunk in chunks)
        {
            Assert.False(string.IsNullOrEmpty(chunk.TextContent));
            Assert.True(chunk.TokenCount <= chunkSize, $"Chunk {chunk.ChunkIndex} exceeds the chunk size.");
            Assert.Equal("hard_split", chunk.MetaData?.ChunkStrategy);
            AssertWellFormedUtf16(chunk.TextContent);

            foreach (string slice in chunk.TextContent.Split("\n\n"))
            {
                Assert.True(slice.Length > 0, $"Chunk {chunk.ChunkIndex} contains an empty slice.");
                Assert.True(slice.Length <= maxCharacters, $"Chunk {chunk.ChunkIndex} contains an oversized slice.");
            }
        }

        if (chunkOverlap == 0)
        {
            string reassembled = string.Concat(chunks.Select(chunk => chunk.TextContent.Replace("\n\n", string.Empty)));
            Assert.Equal(content, reassembled);
        }
    }

    [Fact]
    public void ChunkDocument_HardSplitKeepsSurrogatePairsTogetherAcrossSliceBoundaries()
    {
        // ChunkSize 1 => 4-character slices; the leading "a" shifts every pair so a fixed
        // offset of 4 would land between the high and low surrogate of the second emoji.
        string content = "a" + string.Concat(Enumerable.Repeat(GrinningFace, 5));
        ChunkingService service = CreateChunkingService(chunkSize: 1, chunkOverlap: 0);

        IReadOnlyList<Chunk> chunks = service.ChunkDocument(CreateDocument(), content);

        Assert.Equal("a" + GrinningFace, chunks[0].TextContent);
        Assert.All(chunks, chunk => AssertWellFormedUtf16(chunk.TextContent));
        Assert.Equal(content, string.Concat(chunks.Select(chunk => chunk.TextContent)));
    }

    private static void AssertWellFormedUtf16(string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            char current = text[i];

            if (char.IsHighSurrogate(current))
            {
                Assert.True(
                    i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]),
                    $"Lone high surrogate at index {i}.");
                i++;
                continue;
            }

            Assert.False(char.IsLowSurrogate(current), $"Lone low surrogate at index {i}.");
        }

        // Mirrors the strict encoder used when the text is written as a database parameter.
        var strictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        Exception? exception = Record.Exception(() => strictUtf8.GetBytes(text));
        Assert.Null(exception);
    }

    private static ChunkingService CreateChunkingService(int chunkSize = 100, int chunkOverlap = 0)
    {
        return new ChunkingService(
            Options.Create(new ChunkingSettings
            {
                ChunkSize = chunkSize,
                ChunkOverlap = chunkOverlap
            }),
            NullLogger<ChunkingService>.Instance);
    }

    private static Document CreateDocument()
    {
        return new Document
        {
            DocumentId = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            Title = "Chunking test document",
            ContentHash = "chunking-test-hash"
        };
    }
}
