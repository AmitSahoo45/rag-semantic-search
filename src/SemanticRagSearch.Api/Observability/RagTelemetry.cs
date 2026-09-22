using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace SemanticRagSearch.Api.Observability;

public static class RagTelemetry
{
    public const string InstrumentationName = "SemanticRagSearch";

    public static readonly ActivitySource ActivitySource = new(InstrumentationName);

    public static readonly Meter Meter = new(InstrumentationName);

    private static readonly Counter<long> OperationCalls = Meter.CreateCounter<long>(
        "rag.operation.calls",
        unit: "{call}",
        description: "Completed semantic RAG operations.");

    private static readonly Counter<long> OperationFailures = Meter.CreateCounter<long>(
        "rag.operation.failures",
        unit: "{failure}",
        description: "Failed or cancelled semantic RAG operations.");

    private static readonly Histogram<double> OperationDuration = Meter.CreateHistogram<double>(
        "rag.operation.duration",
        unit: "ms",
        description: "Semantic RAG operation latency.");

    private static readonly Histogram<double> FirstTokenDuration = Meter.CreateHistogram<double>(
        "rag.stream.first_token.duration",
        unit: "ms",
        description: "Time until a streaming operation emits its first non-empty text fragment.");

    public static Activity? StartOperation(string operation)
    {
        Activity? activity = ActivitySource.StartActivity(operation, ActivityKind.Internal);
        activity?.SetTag("rag.operation", operation);
        return activity;
    }

    public static void CompleteOperation(
        Activity? activity,
        string operation,
        string outcome,
        long startedTimestamp)
    {
        var tags = new TagList
        {
            { "rag.operation", operation },
            { "rag.outcome", outcome }
        };
        double elapsedMilliseconds = Stopwatch.GetElapsedTime(startedTimestamp).TotalMilliseconds;

        OperationCalls.Add(1, tags);
        OperationDuration.Record(elapsedMilliseconds, tags);
        if (!string.Equals(outcome, "success", StringComparison.Ordinal))
            OperationFailures.Add(1, tags);

        if (activity is not null)
        {
            activity.SetTag("rag.outcome", outcome);
            activity.SetStatus(
                string.Equals(outcome, "success", StringComparison.Ordinal)
                    ? ActivityStatusCode.Ok
                    : ActivityStatusCode.Error);
            activity.Stop();
        }
    }

    public static void RecordFirstToken(string operation, long startedTimestamp)
    {
        var tags = new TagList
        {
            { "rag.operation", operation },
            { "rag.outcome", "success" }
        };
        FirstTokenDuration.Record(
            Stopwatch.GetElapsedTime(startedTimestamp).TotalMilliseconds,
            tags);
    }
}
