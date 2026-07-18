using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using SemanticRagSearch.Api.Configuration;
using SemanticRagSearch.Api.Generation;
using SemanticRagSearch.Api.Rag;
using SemanticRagSearch.Api.Search;

namespace SemanticRagSearch.UnitTests;

public class RagServiceTests
{
    private static readonly Guid TenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public async Task AskStreamAsync_StreamsFragmentsThenEmitsCompletionMetadata()
    {
        var semanticSearch = new Mock<ISemanticSearchService>(MockBehavior.Strict);
        var textGeneration = new Mock<ITextGenerationService>(MockBehavior.Strict);
        Guid documentId = Guid.NewGuid();
        string? systemPrompt = null;
        string? userPrompt = null;

        semanticSearch
            .Setup(service => service.SearchAsync(
                It.Is<SemanticSearchRequest>(request =>
                    request.TenantId == TenantId &&
                    request.Query == "question" &&
                    request.TopK == 2 &&
                    request.MinSimilarity == 0.5),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SemanticSearchResponse(
                TenantId,
                "question",
                2,
                0.5,
                [new SemanticSearchMatch(
                    Guid.NewGuid(),
                    documentId,
                    "Grounding document",
                    "https://example.test/source",
                    0,
                    "first source",
                    0.9)]));

        textGeneration
            .Setup(service => service.GenerateStreamAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((system, user, _) =>
            {
                systemPrompt = system;
                userPrompt = user;
            })
            .Returns(StreamFragments("", "Grounded", " answer"));

        RagService service = CreateService(semanticSearch.Object, textGeneration.Object);

        List<RagStreamEvent> events = await CollectAsync(
            service.AskStreamAsync(new RagRequest(TenantId, "question", 2, 0.5)));

        Assert.Equal(3, events.Count);
        Assert.Equal("Grounded", events[0].Text);
        Assert.False(events[0].IsCompleted);
        Assert.Null(events[0].Sources);
        Assert.Null(events[0].Metadata);
        Assert.Equal(" answer", events[1].Text);
        Assert.False(events[1].IsCompleted);
        Assert.Null(events[1].Sources);
        Assert.Null(events[1].Metadata);
        Assert.Null(events[2].Text);
        Assert.True(events[2].IsCompleted);
        Assert.Single(events[2].Sources!);
        RagExecutionMetadata metadata = Assert.IsType<RagExecutionMetadata>(events[2].Metadata);
        Assert.True(metadata.GenerationLatencyMs >= 0);
        Assert.NotNull(metadata.TimeToFirstTextMs);
        Assert.True(metadata.TimeToFirstTextMs >= 0);
        Assert.Contains("Answer only from the provided context chunks.", systemPrompt);
        Assert.Contains("[1] Document: Grounding document", userPrompt);
        Assert.Contains("first source", userPrompt);
    }

    [Fact]
    public async Task AskStreamAsync_PropagatesCancellationAfterTextWithoutCompletion()
    {
        var semanticSearch = new Mock<ISemanticSearchService>(MockBehavior.Strict);
        var textGeneration = new Mock<ITextGenerationService>(MockBehavior.Strict);
        using var cancellation = new CancellationTokenSource();
        CancellationToken receivedToken = default;

        semanticSearch
            .Setup(service => service.SearchAsync(
                It.IsAny<SemanticSearchRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(SearchResponseWithOneSource());

        textGeneration
            .Setup(service => service.GenerateStreamAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((_, _, token) => receivedToken = token)
            .Returns(StreamFragments("partial"));

        RagService service = CreateService(semanticSearch.Object, textGeneration.Object);

        await using IAsyncEnumerator<RagStreamEvent> events = service
            .AskStreamAsync(new RagRequest(TenantId, "question", null, null), cancellation.Token)
            .GetAsyncEnumerator();

        Assert.True(await events.MoveNextAsync());
        Assert.Equal("partial", events.Current.Text);

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => events.MoveNextAsync().AsTask());

        Assert.Equal(cancellation.Token, receivedToken);
    }

    [Fact]
    public async Task AskStreamAsync_PropagatesCancellationAfterFallbackWithoutCompletion()
    {
        var semanticSearch = new Mock<ISemanticSearchService>(MockBehavior.Strict);
        var textGeneration = new Mock<ITextGenerationService>(MockBehavior.Strict);
        using var cancellation = new CancellationTokenSource();

        semanticSearch
            .Setup(service => service.SearchAsync(
                It.IsAny<SemanticSearchRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SemanticSearchResponse(
                TenantId,
                "question",
                5,
                0.3,
                []));

        RagService service = CreateService(semanticSearch.Object, textGeneration.Object);

        await using IAsyncEnumerator<RagStreamEvent> events = service
            .AskStreamAsync(new RagRequest(TenantId, "question", null, null), cancellation.Token)
            .GetAsyncEnumerator();

        Assert.True(await events.MoveNextAsync());
        Assert.Equal("I don't know based on the provided documents.", events.Current.Text);

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => events.MoveNextAsync().AsTask());

        textGeneration.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task AskStreamAsync_UsesContextLimitedSourcesInPromptAndCompletion()
    {
        var semanticSearch = new Mock<ISemanticSearchService>(MockBehavior.Strict);
        var textGeneration = new Mock<ITextGenerationService>(MockBehavior.Strict);
        string? userPrompt = null;

        semanticSearch
            .Setup(service => service.SearchAsync(
                It.IsAny<SemanticSearchRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SemanticSearchResponse(
                TenantId,
                "question",
                5,
                0.3,
                [new SemanticSearchMatch(
                    Guid.NewGuid(),
                    Guid.NewGuid(),
                    "Grounding document",
                    null,
                    0,
                    "abcdef",
                    0.9)]));

        textGeneration
            .Setup(service => service.GenerateStreamAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((_, prompt, _) => userPrompt = prompt)
            .Returns(StreamFragments("answer"));

        RagService service = CreateService(
            semanticSearch.Object,
            textGeneration.Object,
            maxContextCharacters: 5);

        List<RagStreamEvent> events = await CollectAsync(
            service.AskStreamAsync(new RagRequest(TenantId, "question", null, null)));

        string prompt = Assert.IsType<string>(userPrompt);
        Assert.Contains("abcde", prompt);
        Assert.DoesNotContain("abcdef", prompt);
        Assert.Equal("abcde", Assert.Single(events[1].Sources!).TextContent);
    }

    [Fact]
    public async Task AskAsync_ReturnsAnswerSourcesAndExecutionMetadata()
    {
        var semanticSearch = new Mock<ISemanticSearchService>(MockBehavior.Strict);
        var textGeneration = new Mock<ITextGenerationService>(MockBehavior.Strict);

        semanticSearch
            .Setup(service => service.SearchAsync(
                It.Is<SemanticSearchRequest>(request =>
                    request.TenantId == TenantId &&
                    request.Query == "question" &&
                    request.TopK == 2 &&
                    request.MinSimilarity == 0.5),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SemanticSearchResponse(
                TenantId,
                "question",
                2,
                0.5,
                [new SemanticSearchMatch(
                    Guid.NewGuid(),
                    Guid.NewGuid(),
                    "Grounding document",
                    null,
                    0,
                    "evidence",
                    0.9)]));

        textGeneration
            .Setup(service => service.GenerateAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync("grounded answer");

        RagService service = CreateService(semanticSearch.Object, textGeneration.Object);

        RagResponse response = await service.AskAsync(
            new RagRequest(TenantId, " question ", 2, 0.5));

        Assert.Equal("question", response.Question);
        Assert.Equal("grounded answer", response.Answer);
        Assert.Single(response.Sources);
        Assert.True(response.Metadata.SearchLatencyMs >= 0);
        Assert.True(response.Metadata.GenerationLatencyMs >= 0);
        Assert.Null(response.Metadata.TimeToFirstTextMs);
    }

    [Fact]
    public async Task AskAsync_ReturnsFallbackMetadataAndSkipsGenerationWhenNoSourcesExist()
    {
        var semanticSearch = new Mock<ISemanticSearchService>(MockBehavior.Strict);
        var textGeneration = new Mock<ITextGenerationService>(MockBehavior.Strict);

        semanticSearch
            .Setup(service => service.SearchAsync(
                It.IsAny<SemanticSearchRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SemanticSearchResponse(
                TenantId,
                "question",
                5,
                0.3,
                []));

        RagService service = CreateService(semanticSearch.Object, textGeneration.Object);

        RagResponse response = await service.AskAsync(
            new RagRequest(TenantId, "question", null, null));

        Assert.Equal("I don't know based on the provided documents.", response.Answer);
        Assert.Empty(response.Sources);
        Assert.Equal(0, response.Metadata.GenerationLatencyMs);
        Assert.Null(response.Metadata.TimeToFirstTextMs);
        textGeneration.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task AskStreamAsync_EmitsFallbackAndSkipsGenerationWhenNoSourcesExist()
    {
        var semanticSearch = new Mock<ISemanticSearchService>(MockBehavior.Strict);
        var textGeneration = new Mock<ITextGenerationService>(MockBehavior.Strict);

        semanticSearch
            .Setup(service => service.SearchAsync(
                It.IsAny<SemanticSearchRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SemanticSearchResponse(
                TenantId,
                "question",
                5,
                0.3,
                []));

        RagService service = CreateService(semanticSearch.Object, textGeneration.Object);

        List<RagStreamEvent> events = await CollectAsync(
            service.AskStreamAsync(new RagRequest(TenantId, "question", null, null)));

        Assert.Equal(2, events.Count);
        Assert.Equal("I don't know based on the provided documents.", events[0].Text);
        Assert.False(events[0].IsCompleted);
        Assert.True(events[1].IsCompleted);
        Assert.Empty(events[1].Sources!);
        Assert.Equal(0, events[1].Metadata!.GenerationLatencyMs);
        textGeneration.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task AskStreamAsync_PassesCancellationTokenToSemanticSearch()
    {
        var semanticSearch = new Mock<ISemanticSearchService>(MockBehavior.Strict);
        var textGeneration = new Mock<ITextGenerationService>(MockBehavior.Strict);
        using var cancellation = new CancellationTokenSource();
        CancellationToken receivedToken = default;

        semanticSearch
            .Setup(service => service.SearchAsync(
                It.IsAny<SemanticSearchRequest>(),
                It.IsAny<CancellationToken>()))
            .Callback<SemanticSearchRequest, CancellationToken>((_, token) => receivedToken = token)
            .Returns<SemanticSearchRequest, CancellationToken>(
                (_, token) => Task.FromCanceled<SemanticSearchResponse>(token));

        RagService service = CreateService(semanticSearch.Object, textGeneration.Object);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CollectAsync(service.AskStreamAsync(
                new RagRequest(TenantId, "question", null, null),
                cancellation.Token)));

        Assert.Equal(cancellation.Token, receivedToken);
        textGeneration.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task AskStreamAsync_PropagatesProviderFailureWithoutCompletionEvent()
    {
        var semanticSearch = new Mock<ISemanticSearchService>(MockBehavior.Strict);
        var textGeneration = new Mock<ITextGenerationService>(MockBehavior.Strict);

        semanticSearch
            .Setup(service => service.SearchAsync(
                It.IsAny<SemanticSearchRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(SearchResponseWithOneSource());

        textGeneration
            .Setup(service => service.GenerateStreamAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Returns(StreamThenThrow("partial", new InvalidOperationException("provider failed")));

        RagService service = CreateService(semanticSearch.Object, textGeneration.Object);

        await using IAsyncEnumerator<RagStreamEvent> events = service
            .AskStreamAsync(new RagRequest(TenantId, "question", null, null))
            .GetAsyncEnumerator();

        Assert.True(await events.MoveNextAsync());
        Assert.Equal("partial", events.Current.Text);
        Assert.False(events.Current.IsCompleted);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => events.MoveNextAsync().AsTask());

        Assert.Equal("provider failed", exception.Message);
    }

    private static RagService CreateService(
        ISemanticSearchService semanticSearch,
        ITextGenerationService textGeneration,
        int maxContextCharacters = 12_000)
    {
        return new RagService(
            semanticSearch,
            textGeneration,
            Options.Create(new RagSettings
            {
                MaxQuestionLength = 2_000,
                MaxContextCharacters = maxContextCharacters
            }),
            NullLogger<RagService>.Instance);
    }

    private static async Task<List<T>> CollectAsync<T>(IAsyncEnumerable<T> values)
    {
        var result = new List<T>();

        await foreach (T value in values)
            result.Add(value);

        return result;
    }

    private static async IAsyncEnumerable<string> StreamFragments(params string[] fragments)
    {
        foreach (string fragment in fragments)
            yield return fragment;

        await Task.CompletedTask;
    }

    private static async IAsyncEnumerable<string> StreamThenThrow(
        string fragment,
        Exception exception)
    {
        yield return fragment;
        await Task.Yield();
        throw exception;
    }

    private static SemanticSearchResponse SearchResponseWithOneSource()
    {
        return new SemanticSearchResponse(
            TenantId,
            "question",
            5,
            0.3,
            [new SemanticSearchMatch(
                Guid.NewGuid(),
                Guid.NewGuid(),
                "Grounding document",
                null,
                0,
                "evidence",
                0.9)]);
    }
}
