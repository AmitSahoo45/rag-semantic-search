using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SemanticRagSearch.Api.Evaluation;

public static class RetrievalEvaluationDatasetLoader
{
    public static async Task<RetrievalEvaluationDataset> LoadAsync(
        string datasetPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(datasetPath);
        cancellationToken.ThrowIfCancellationRequested();
        var fullDatasetPath = Path.GetFullPath(datasetPath);
        var directory = Path.GetDirectoryName(fullDatasetPath)!;
        WireDataset dataset;
        try
        {
            await using var stream = File.OpenRead(fullDatasetPath);
            dataset = await JsonSerializer.DeserializeAsync<WireDataset>(stream,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, cancellationToken)
                ?? throw new InvalidDataException("Dataset must be a JSON object.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Dataset contains invalid JSON or invalid field types.", exception);
        }

        var name = RequireText(dataset.Name, "Dataset name");
        if (dataset.Documents is not { Count: > 0 })
            throw new InvalidDataException("Dataset must contain documents.");
        if (dataset.Queries is not { Count: > 0 })
            throw new InvalidDataException("Dataset must contain judged queries.");

        var documentIds = new HashSet<string>(StringComparer.Ordinal);
        var contentHashes = new HashSet<string>(StringComparer.Ordinal);
        var documents = new List<RetrievalEvaluationDocument>();
        foreach (var document in dataset.Documents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (document is null) throw new InvalidDataException("Document cannot be null.");
            var id = RequireUniqueId(document.Id, documentIds, "Document");
            var title = RequireBoundedText(document.Title, $"Document '{id}' title", 300);
            var relativePath = RequireText(document.Path, $"Document '{id}' path");
            var fullPath = ResolveDocumentPath(directory, relativePath);
            var content = await File.ReadAllTextAsync(fullPath, cancellationToken);
            if (string.IsNullOrWhiteSpace(content))
                throw new InvalidDataException($"Document '{id}' must have content.");
            var contentHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
            if (!contentHashes.Add(contentHash))
                throw new InvalidDataException($"Document '{id}' duplicates another document's content; ingestion would merge their IDs.");
            documents.Add(new RetrievalEvaluationDocument(id, title, relativePath, content));
        }

        var queryIds = new HashSet<string>(StringComparer.Ordinal);
        var queries = new List<RetrievalEvaluationQuery>();
        foreach (var query in dataset.Queries)
        {
            if (query is null) throw new InvalidDataException("Query cannot be null.");
            var id = RequireUniqueId(query.Id, queryIds, "Query");
            var category = RequireText(query.Category, $"Query '{id}' category");
            var question = RequireBoundedText(query.Query, $"Query '{id}' question", 2000);
            if (query.RelevantDocumentIds is not { Count: > 0 })
                throw new InvalidDataException($"Query '{id}' must have relevance judgments.");
            var relevant = new HashSet<string>(StringComparer.Ordinal);
            foreach (var judgment in query.RelevantDocumentIds)
            {
                var documentId = RequireUniqueId(judgment, relevant, $"Query '{id}' judgment");
                if (!documentIds.Contains(documentId))
                    throw new InvalidDataException($"Query '{id}' references unknown document '{documentId}'.");
            }
            queries.Add(new RetrievalEvaluationQuery(id, category, question, query.RelevantDocumentIds.AsReadOnly()));
        }

        var probes = new List<RetrievalEvaluationProbe>();
        foreach (var probe in dataset.UnanswerableQueries ?? [])
        {
            if (probe is null) throw new InvalidDataException("Unanswerable query cannot be null.");
            var id = RequireUniqueId(probe.Id, queryIds, "Unanswerable query");
            probes.Add(new RetrievalEvaluationProbe(id, RequireBoundedText(probe.Query, $"Unanswerable query '{id}' question", 2000)));
        }

        return new RetrievalEvaluationDataset(name, documents.AsReadOnly(), queries.AsReadOnly(), probes.AsReadOnly());
    }

    private static string RequireText(string? value, string label)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new InvalidDataException($"{label} must be nonempty.");
        return value;
    }

    private static string RequireUniqueId(string? value, HashSet<string> ids, string label)
    {
        var id = RequireText(value, $"{label} ID");
        if (!ids.Add(id)) throw new InvalidDataException($"{label} ID '{id}' must be unique.");
        return id;
    }

    private static string RequireBoundedText(string? value, string label, int maximumLength)
    {
        var text = RequireText(value, label);
        if (text.Trim().Length > maximumLength)
            throw new InvalidDataException($"{label} cannot exceed {maximumLength} characters.");
        return text;
    }

    private static string ResolveDocumentPath(string directory, string relativePath)
    {
        if (Path.IsPathRooted(relativePath)
            || relativePath.Replace('\\', '/').Split('/').Any(segment => segment is "." or ".."))
        {
            throw new InvalidDataException($"Document path '{relativePath}' must be relative without traversal.");
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(Path.Combine(directory, relativePath));
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException($"Document path '{relativePath}' is invalid.", exception);
        }

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var prefix = GetContainmentPrefix(directory);
        if (!fullPath.StartsWith(prefix, comparison) || !File.Exists(fullPath))
            throw new InvalidDataException($"Document path '{relativePath}' must reference an existing file inside the dataset directory.");

        // A relative path can still escape through a symbolic link or Windows junction.
        // Only components strictly below the dataset directory are checked: the dataset
        // directory itself may legitimately be a link, and nothing escapes through it.
        var current = fullPath;
        while (current is not null && !string.Equals(current, directory, comparison))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"Document path '{relativePath}' cannot contain symbolic links or junctions.");
            current = Path.GetDirectoryName(current);
        }

        return fullPath;
    }

    /// <summary>
    /// Builds the prefix every path contained in <paramref name="directory"/> must start with.
    /// A filesystem root such as <c>E:\</c> or <c>/</c> already ends in a separator, so one is
    /// appended only when missing; trimming and re-adding would double it on roots.
    /// </summary>
    public static string GetContainmentPrefix(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        return Path.EndsInDirectorySeparator(directory) ? directory : directory + Path.DirectorySeparatorChar;
    }

    private sealed class WireDataset
    {
        public string? Name { get; set; }
        public List<WireDocument?>? Documents { get; set; }
        public List<WireQuery?>? Queries { get; set; }
        public List<WireProbe?>? UnanswerableQueries { get; set; }
    }

    private sealed class WireDocument
    {
        public string? Id { get; set; }
        public string? Title { get; set; }
        public string? Path { get; set; }
    }

    private sealed class WireQuery
    {
        public string? Id { get; set; }
        public string? Category { get; set; }
        public string? Query { get; set; }
        public List<string>? RelevantDocumentIds { get; set; }
    }

    private sealed class WireProbe
    {
        public string? Id { get; set; }
        public string? Query { get; set; }
    }
}
