namespace SemanticRagSearch.Api.Evaluation;

public sealed record RetrievalMetricResult(
    double Recall,
    double ReciprocalRank,
    double Ndcg,
    IReadOnlyList<string> RetrievedDocumentIds);

public static class RetrievalMetrics
{
    public static RetrievalMetricResult Calculate(
        IReadOnlyList<string> retrievedDocumentIds,
        IReadOnlyCollection<string> relevantDocumentIds,
        int topK)
    {
        ArgumentNullException.ThrowIfNull(retrievedDocumentIds);
        ArgumentNullException.ThrowIfNull(relevantDocumentIds);
        ArgumentOutOfRangeException.ThrowIfLessThan(topK, 1);
        if (retrievedDocumentIds.Any(string.IsNullOrWhiteSpace)
            || relevantDocumentIds.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("Document IDs must be nonempty.");
        }

        var relevant = relevantDocumentIds.ToHashSet(StringComparer.Ordinal);
        if (relevant.Count == 0)
        {
            throw new ArgumentException("Judged queries must have at least one relevant document.", nameof(relevantDocumentIds));
        }

        // Search returns chunks. Rank each document once, before applying document TopK.
        var retrieved = retrievedDocumentIds.Distinct(StringComparer.Ordinal).Take(topK).ToArray();
        var hits = 0;
        var reciprocalRank = 0d;
        var discountedGain = 0d;
        for (var index = 0; index < retrieved.Length; index++)
        {
            if (!relevant.Contains(retrieved[index])) continue;
            hits++;
            if (reciprocalRank == 0) reciprocalRank = 1d / (index + 1);
            discountedGain += 1d / Math.Log2(index + 2);
        }

        var idealGain = 0d;
        for (var index = 0; index < Math.Min(topK, relevant.Count); index++)
        {
            idealGain += 1d / Math.Log2(index + 2);
        }

        return new RetrievalMetricResult(
            (double)hits / relevant.Count,
            reciprocalRank,
            discountedGain / idealGain,
            Array.AsReadOnly(retrieved));
    }
}
