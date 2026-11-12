using System.Text.Json;
using FluentAssertions;
using SemanticRagSearch.Api.Evaluation;

namespace SemanticRagSearch.UnitTests;

public sealed class RetrievalEvaluationTests
{
    [Fact]
    public void Calculate_DeduplicatesChunksBeforeApplyingDocumentTopK()
    {
        var result = RetrievalMetrics.Calculate(["noise", "noise", "a", "a", "b"], ["a", "b"], 3);

        result.RetrievedDocumentIds.Should().Equal("noise", "a", "b");
        result.Recall.Should().Be(1);
        result.ReciprocalRank.Should().Be(0.5);
        result.Ndcg.Should().BeApproximately(0.6934264036172708, 1e-12);
    }

    [Fact]
    public void Calculate_UsesAllRelevantDocumentsForRecallAndIdealRanking()
    {
        var result = RetrievalMetrics.Calculate(["a", "noise", "b"], ["a", "b", "c"], 2);

        result.RetrievedDocumentIds.Should().Equal("a", "noise");
        result.Recall.Should().BeApproximately(1d / 3, 1e-12);
        result.ReciprocalRank.Should().Be(1);
        result.Ndcg.Should().BeApproximately(0.6131471927654584, 1e-12);
    }

    [Fact]
    public void Calculate_ReturnsZeroMetricsWhenRetrievalIsEmpty()
    {
        var result = RetrievalMetrics.Calculate([], ["a"], 5);

        result.Recall.Should().Be(0);
        result.ReciprocalRank.Should().Be(0);
        result.Ndcg.Should().Be(0);
        result.RetrievedDocumentIds.Should().BeEmpty();
    }

    [Fact]
    public void Calculate_DoesNotGiveCreditForRelevantDocumentsBeyondTopK()
    {
        var result = RetrievalMetrics.Calculate(["noise", "a"], ["a"], 1);

        result.Recall.Should().Be(0);
        result.ReciprocalRank.Should().Be(0);
        result.Ndcg.Should().Be(0);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Calculate_RejectsNonpositiveTopK(int topK)
    {
        var calculate = () => RetrievalMetrics.Calculate(["a"], ["a"], topK);

        calculate.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Calculate_RejectsMissingJudgmentsSoProbesCannotBecomeAnswerableMetrics()
    {
        var calculate = () => RetrievalMetrics.Calculate(["a"], [], 5);

        calculate.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Calculate_TreatsDocumentIdsAsOrdinalAndJudgmentsAsASet()
    {
        var result = RetrievalMetrics.Calculate(["A", "a"], ["a", "a"], 2);

        result.Recall.Should().Be(1);
        result.ReciprocalRank.Should().Be(0.5);
        result.Ndcg.Should().BeApproximately(0.6309297535714574, 1e-12);
    }

    [Fact]
    public async Task LoadAsync_LoadsDocumentContentAndKeepsProbesSeparate()
    {
        using var fixture = new DatasetFixture();

        var dataset = await RetrievalEvaluationDatasetLoader.LoadAsync(fixture.DatasetPath);

        dataset.Name.Should().Be("Sample corpus");
        dataset.Documents.Should().ContainSingle().Which.Content.Should().Be("# Test document\nUseful content.");
        dataset.Documents[0].Path.Should().Be("docs/a.md");
        dataset.Queries.Should().ContainSingle().Which.RelevantDocumentIds.Should().Equal("a");
        dataset.UnanswerableQueries.Should().ContainSingle().Which.Id.Should().Be("probe");
    }

    [Theory]
    [InlineData("duplicateDocument")]
    [InlineData("duplicateQuery")]
    [InlineData("duplicateProbe")]
    [InlineData("crossQueryProbeId")]
    [InlineData("unknownJudgment")]
    [InlineData("duplicateJudgment")]
    [InlineData("emptyJudgments")]
    [InlineData("blankDocumentId")]
    [InlineData("blankQuestion")]
    [InlineData("blankProbe")]
    [InlineData("missingDocuments")]
    [InlineData("missingQueries")]
    [InlineData("duplicateContent")]
    [InlineData("longTitle")]
    [InlineData("longQuestion")]
    [InlineData("longProbe")]
    public async Task LoadAsync_RejectsInvalidDatasetJudgmentsAndIdentity(string mutation)
    {
        using var fixture = new DatasetFixture(mutation);

        var load = () => RetrievalEvaluationDatasetLoader.LoadAsync(fixture.DatasetPath);

        await load.Should().ThrowAsync<InvalidDataException>();
    }

    [Theory]
    [InlineData("../outside.md")]
    [InlineData("docs/../../outside.md")]
    [InlineData("/outside.md")]
    [InlineData("missing.md")]
    public async Task LoadAsync_RejectsUnsafeOrMissingDocumentPaths(string path)
    {
        using var fixture = new DatasetFixture(documentPath: path);

        var load = () => RetrievalEvaluationDatasetLoader.LoadAsync(fixture.DatasetPath);

        await load.Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task LoadAsync_RejectsAbsoluteDocumentPath()
    {
        using var fixture = new DatasetFixture(documentPath: Path.GetFullPath("outside.md"));

        var load = () => RetrievalEvaluationDatasetLoader.LoadAsync(fixture.DatasetPath);

        await load.Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task LoadAsync_HonorsCancellation()
    {
        using var fixture = new DatasetFixture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var load = () => RetrievalEvaluationDatasetLoader.LoadAsync(fixture.DatasetPath, cancellation.Token);

        await load.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task LoadAsync_AcceptsIngestionAndSearchMaximumLengths()
    {
        using var fixture = new DatasetFixture("boundaryLengths");

        var dataset = await RetrievalEvaluationDatasetLoader.LoadAsync(fixture.DatasetPath);

        dataset.Documents[0].Title.Length.Should().Be(300);
        dataset.Queries[0].Query.Length.Should().Be(2000);
        dataset.UnanswerableQueries[0].Query.Length.Should().Be(2000);
    }

    [Fact]
    public void GetContainmentPrefix_DoesNotDoubleSeparatorForFilesystemRoot()
    {
        var root = Path.GetPathRoot(Path.GetTempPath())!;
        var documentInsideRoot = Path.GetFullPath(Path.Combine(root, "docs", "a.md"));

        var prefix = RetrievalEvaluationDatasetLoader.GetContainmentPrefix(root);

        Path.EndsInDirectorySeparator(prefix).Should().BeTrue();
        prefix.Should().NotEndWith($"{Path.DirectorySeparatorChar}{Path.DirectorySeparatorChar}");
        documentInsideRoot.Should().StartWith(prefix);
    }

    [Fact]
    public void GetContainmentPrefix_AppendsSingleSeparatorToNormalDirectory()
    {
        var directory = Path.TrimEndingDirectorySeparator(Path.Combine(Path.GetTempPath(), "retrieval-evaluation-tests"));
        var documentInside = Path.Combine(directory, "docs", "a.md");
        var sibling = directory + "-other" + Path.DirectorySeparatorChar + "a.md";

        var prefix = RetrievalEvaluationDatasetLoader.GetContainmentPrefix(directory);

        prefix.Should().Be(directory + Path.DirectorySeparatorChar);
        documentInside.Should().StartWith(prefix);
        sibling.Should().NotStartWith(prefix);
    }

    [Fact]
    public void GetContainmentPrefix_KeepsExistingTrailingSeparator()
    {
        var directory = Path.Combine(Path.GetTempPath(), "retrieval-evaluation-tests") + Path.DirectorySeparatorChar;

        var prefix = RetrievalEvaluationDatasetLoader.GetContainmentPrefix(directory);

        prefix.Should().Be(directory);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void GetContainmentPrefix_RejectsBlankDirectory(string directory)
    {
        var getPrefix = () => RetrievalEvaluationDatasetLoader.GetContainmentPrefix(directory);

        getPrefix.Should().Throw<ArgumentException>();
    }

    private sealed class DatasetFixture : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "retrieval-evaluation-tests", Guid.NewGuid().ToString("N"));

        public string DatasetPath => Path.Combine(directory, "dataset.json");

        public DatasetFixture(string? mutation = null, string documentPath = "docs/a.md")
        {
            Directory.CreateDirectory(Path.Combine(directory, "docs"));
            File.WriteAllText(Path.Combine(directory, "docs", "a.md"), "# Test document\nUseful content.");
            var title = mutation switch { "longTitle" => new string('t', 301), "boundaryLengths" => new string('t', 300), _ => "Test" };
            var question = mutation switch { "blankQuestion" => " ", "longQuestion" => new string('q', 2001), "boundaryLengths" => new string('q', 2000), _ => "What is useful?" };
            var probeQuestion = mutation switch { "blankProbe" => " ", "longProbe" => new string('p', 2001), "boundaryLengths" => new string('p', 2000), _ => "Unsupported question?" };
            var documents = new List<object> { new { id = mutation == "blankDocumentId" ? " " : "a", title, path = documentPath } };
            var judgments = mutation switch
            {
                "unknownJudgment" => new[] { "missing" },
                "duplicateJudgment" => new[] { "a", "a" },
                "emptyJudgments" => [],
                _ => new[] { "a" }
            };
            var queries = new List<object> { new { id = "q", category = "terminology", query = question, relevantDocumentIds = judgments } };
            var probes = new List<object> { new { id = mutation == "crossQueryProbeId" ? "q" : "probe", query = probeQuestion } };
            if (mutation == "duplicateDocument") documents.Add(documents[0]);
            if (mutation == "duplicateQuery") queries.Add(queries[0]);
            if (mutation == "duplicateProbe") probes.Add(probes[0]);
            if (mutation == "missingDocuments") documents.Clear();
            if (mutation == "missingQueries") queries.Clear();
            if (mutation == "duplicateContent")
            {
                File.WriteAllText(Path.Combine(directory, "docs", "b.md"), "# Test document\nUseful content.");
                documents.Add(new { id = "b", title = "Different ID", path = "docs/b.md" });
            }
            File.WriteAllText(DatasetPath, JsonSerializer.Serialize(new { name = "Sample corpus", documents, queries, unanswerableQueries = probes }));
        }

        public void Dispose() => Directory.Delete(directory, recursive: true);
    }
}
