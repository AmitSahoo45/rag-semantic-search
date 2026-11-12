namespace SemanticRagSearch.Api.Evaluation;

public sealed record RetrievalEvaluationDataset(
    string Name,
    IReadOnlyList<RetrievalEvaluationDocument> Documents,
    IReadOnlyList<RetrievalEvaluationQuery> Queries,
    IReadOnlyList<RetrievalEvaluationProbe> UnanswerableQueries);

public sealed record RetrievalEvaluationDocument(string Id, string Title, string Path, string Content);

public sealed record RetrievalEvaluationQuery(
    string Id,
    string Category,
    string Query,
    IReadOnlyList<string> RelevantDocumentIds);

public sealed record RetrievalEvaluationProbe(string Id, string Query);
