using System.Reflection;
using System.Net.ServerSentEvents;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using SemanticRagSearch.Api.Controllers;
using SemanticRagSearch.Api.Rag;

namespace SemanticRagSearch.UnitTests;

public class RagControllerTests
{
    private static readonly Guid TenantId =
        Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public void AskStream_UsesPostStreamRouteAndSseContentType()
    {
        MethodInfo action = typeof(RagController).GetMethod(
            nameof(RagController.AskStream))!;

        HttpPostAttribute httpPost = Assert.Single(
            action.GetCustomAttributes<HttpPostAttribute>());
        ProducesAttribute produces = Assert.Single(
            action.GetCustomAttributes<ProducesAttribute>());

        Assert.Equal("stream", httpPost.Template);
        Assert.Contains("text/event-stream", produces.ContentTypes);
    }

    [Fact]
    public async Task AskStream_MissingBodyReturnsBadRequestWithoutCallingService()
    {
        var ragService = new Mock<IRagService>(MockBehavior.Strict);
        var controller = new RagController(ragService.Object);

        IResult result = controller.AskStream(null, CancellationToken.None);
        DefaultHttpContext context = CreateHttpContext();

        await result.ExecuteAsync(context);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        context.Response.Body.Position = 0;
        using JsonDocument body = await JsonDocument.ParseAsync(context.Response.Body);
        Assert.Equal(
            "Request body is required.",
            body.RootElement.GetProperty("error").GetString());
        ragService.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task AskStream_WritesOrderedTextAndCompletionEvents()
    {
        var request = new RagRequest(TenantId, "question", 3, 0.4);
        var source = new RagSource(
            1,
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            Guid.Parse("33333333-3333-3333-3333-333333333333"),
            "Document",
            "https://example.test/document",
            0,
            0.9,
            "evidence");
        var metadata = new RagExecutionMetadata(12, 34, 5);
        var expectedEvents = new[]
        {
            new RagStreamEvent("Grounded", false, null, null),
            new RagStreamEvent(" answer", false, null, null),
            new RagStreamEvent(null, true, new[] { source }, metadata)
        };
        var ragService = new Mock<IRagService>(MockBehavior.Strict);
        ragService
            .Setup(service => service.AskStreamAsync(
                request,
                It.IsAny<CancellationToken>()))
            .Returns(ToAsyncEnumerable(expectedEvents));
        var controller = new RagController(ragService.Object);
        DefaultHttpContext context = CreateHttpContext();

        IResult result = controller.AskStream(request, CancellationToken.None);
        await result.ExecuteAsync(context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.StartsWith("text/event-stream", context.Response.ContentType);
        context.Response.Body.Position = 0;
        var parsedItems = new List<SseItem<string>>();
        await foreach (SseItem<string> item in SseParser
            .Create(context.Response.Body)
            .EnumerateAsync())
        {
            parsedItems.Add(item);
        }

        Assert.Equal(3, parsedItems.Count);
        Assert.All(
            parsedItems,
            item => Assert.Equal(SseParser.EventTypeDefault, item.EventType));
        RagStreamEvent[] actualEvents = parsedItems
            .Select(item => JsonSerializer.Deserialize<RagStreamEvent>(
                item.Data,
                new JsonSerializerOptions(JsonSerializerDefaults.Web))!)
            .ToArray();

        Assert.Equal("Grounded", actualEvents[0].Text);
        Assert.False(actualEvents[0].IsCompleted);
        Assert.Equal(" answer", actualEvents[1].Text);
        Assert.False(actualEvents[1].IsCompleted);
        Assert.Null(actualEvents[2].Text);
        Assert.True(actualEvents[2].IsCompleted);
        Assert.Equal(source, Assert.Single(actualEvents[2].Sources!));
        Assert.Equal(metadata, actualEvents[2].Metadata);
    }

    [Fact]
    public async Task AskStream_PassesRequestCancellationTokenToRagService()
    {
        var request = new RagRequest(TenantId, "question", null, null);
        using var cancellation = new CancellationTokenSource();
        CancellationToken observedToken = default;
        var ragService = new Mock<IRagService>(MockBehavior.Strict);
        ragService
            .Setup(service => service.AskStreamAsync(
                request,
                It.IsAny<CancellationToken>()))
            .Callback<RagRequest, CancellationToken>(
                (_, token) => observedToken = token)
            .Returns(ToAsyncEnumerable([]));
        var controller = new RagController(ragService.Object);

        IResult result = controller.AskStream(request, cancellation.Token);
        await result.ExecuteAsync(CreateHttpContext());

        Assert.Equal(cancellation.Token, observedToken);
    }

    private static async IAsyncEnumerable<RagStreamEvent> ToAsyncEnumerable(
        IEnumerable<RagStreamEvent> events)
    {
        foreach (RagStreamEvent streamEvent in events)
        {
            yield return streamEvent;
            await Task.Yield();
        }
    }

    private static DefaultHttpContext CreateHttpContext()
    {
        ServiceProvider services = new ServiceCollection()
            .AddLogging()
            .ConfigureHttpJsonOptions(options =>
            {
                options.SerializerOptions.PropertyNamingPolicy =
                    JsonNamingPolicy.CamelCase;
            })
            .BuildServiceProvider();

        return new DefaultHttpContext
        {
            RequestServices = services,
            Response = { Body = new MemoryStream() }
        };
    }
}
