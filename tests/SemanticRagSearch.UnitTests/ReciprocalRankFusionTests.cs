using Pgvector;
using SemanticRagSearch.Api.Domain;
using SemanticRagSearch.Api.Search;
using SemanticRagSearch.Api.VectorStore;

namespace SemanticRagSearch.UnitTests;

public sealed class ReciprocalRankFusionTests
{
    [Fact]
    public void Fuse_CombinesRanksDeduplicatesAndPreservesCosineScore()
    {
        SimilarityResult first = Candidate(1, 0.95);
        SimilarityResult shared = Candidate(2, 0.6);
        SimilarityResult lexical = Candidate(3, 0.4);

        IReadOnlyList<RankedSearchResult> results = ReciprocalRankFusion.Fuse(
            [first, shared], [shared, lexical, shared], 3, 60);

        Assert.Equal(new[] { shared.Chunk.ChunkId, first.Chunk.ChunkId, lexical.Chunk.ChunkId },
            results.Select(result => result.Result.Chunk.ChunkId));
        Assert.Equal(1.0 / 62 + 1.0 / 61, results[0].RankingScore, 12);
        Assert.Equal(0.6, results[0].Result.Score);
    }

    [Fact]
    public void Fuse_BreaksTiesByChunkIdAndLimitsResults()
    {
        SimilarityResult low = Candidate(1, 0.7);
        SimilarityResult high = Candidate(2, 0.9);
        Assert.Equal(low.Chunk.ChunkId,
            Assert.Single(ReciprocalRankFusion.Fuse([high], [low], 1, 60)).Result.Chunk.ChunkId);
        Assert.Empty(ReciprocalRankFusion.Fuse([], [], 5, 60));
    }

    private static SimilarityResult Candidate(int id, double score) => new(new Chunk
    {
        ChunkId = Guid.Parse($"00000000-0000-0000-0000-{id:D12}"),
        TextContent = "candidate",
        Embedding = new Vector(new[] { 1f })
    }, score);
}
