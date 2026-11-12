using SemanticRagSearch.Api.VectorStore;

namespace SemanticRagSearch.Api.Search;

public sealed record RankedSearchResult(SimilarityResult Result, double RankingScore);

public static class ReciprocalRankFusion
{
    public static IReadOnlyList<RankedSearchResult> Fuse(
        IReadOnlyList<SimilarityResult> vectorCandidates,
        IReadOnlyList<SimilarityResult> lexicalCandidates,
        int topK,
        int rrfConstant)
    {
        ArgumentNullException.ThrowIfNull(vectorCandidates);
        ArgumentNullException.ThrowIfNull(lexicalCandidates);
        if (topK <= 0) throw new ArgumentOutOfRangeException(nameof(topK));
        if (rrfConstant <= 0) throw new ArgumentOutOfRangeException(nameof(rrfConstant));

        var fused = new Dictionary<Guid, RankedSearchResult>();
        AddBranch(vectorCandidates);
        AddBranch(lexicalCandidates);
        return fused.Values.OrderByDescending(result => result.RankingScore)
            .ThenBy(result => result.Result.Chunk.ChunkId).Take(topK).ToArray();

        void AddBranch(IReadOnlyList<SimilarityResult> candidates)
        {
            var seen = new HashSet<Guid>();
            int rank = 0;
            foreach (SimilarityResult candidate in candidates)
            {
                if (!seen.Add(candidate.Chunk.ChunkId)) continue;
                rank++;
                double contribution = 1.0 / ((double)rrfConstant + rank);
                if (fused.TryGetValue(candidate.Chunk.ChunkId, out RankedSearchResult? previous))
                    fused[candidate.Chunk.ChunkId] = previous with { RankingScore = previous.RankingScore + contribution };
                else
                    fused.Add(candidate.Chunk.ChunkId, new RankedSearchResult(candidate, contribution));
            }
        }
    }
}
