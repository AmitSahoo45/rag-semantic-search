using System.Globalization;

namespace SemanticRagSearch.Api.Evaluation;

public sealed record EvaluationRunOptions(string DatasetPath, string OutputDirectory, int TopK, double MinSimilarity)
{
    public static EvaluationRunOptions Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Length % 2 != 0)
            throw new ArgumentException("Each evaluation option requires a value. Use --help for usage.");
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < args.Length; i += 2)
        {
            if (args[i] is not ("--dataset" or "--output" or "--top-k" or "--min-similarity") ||
                string.IsNullOrWhiteSpace(args[i + 1]) || !values.TryAdd(args[i], args[i + 1]))
                throw new ArgumentException("Unknown, duplicate, or empty evaluation option. Use --help for usage.");
        }
        if (!int.TryParse(values.GetValueOrDefault("--top-k", "3"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int topK) ||
            topK is < 1 or > 20)
            throw new ArgumentException("--top-k must be an integer from 1 to 20.");
        if (!double.TryParse(values.GetValueOrDefault("--min-similarity", "0.3"), NumberStyles.Float, CultureInfo.InvariantCulture, out double similarity) ||
            !double.IsFinite(similarity) || similarity is < 0 or > 1)
            throw new ArgumentException("--min-similarity must be a finite number from 0 to 1.");
        return new(values.GetValueOrDefault("--dataset", "samples/phase4/dataset.json"),
            values.GetValueOrDefault("--output", "artifacts/phase4"), topK, similarity);
    }
}
