using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SemanticRagSearch.Api.Errors;

namespace SemanticRagSearch.UnitTests;

public class ApiErrorMappingTests
{
    [Fact]
    public async Task TryHandleAsync_MapsPayloadTooLargeToSanitized413()
    {
        var handler = new GlobalExceptionHandler(NullLogger<GlobalExceptionHandler>.Instance);
        DefaultHttpContext context = CreateHttpContext();
        var exception = new BadHttpRequestException(
            "Request body too large. The max request body size is 1048576 bytes.",
            StatusCodes.Status413PayloadTooLarge);

        bool handled = await handler.TryHandleAsync(context, exception, CancellationToken.None);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status413PayloadTooLarge, context.Response.StatusCode);
        ApiErrorResponse body = await ReadBodyAsync(context);
        Assert.Equal("Request body is too large.", body.Error);
    }

    [Fact]
    public async Task TryHandleAsync_MapsOtherBadHttpRequestsToTheirStatusCodeWithoutEchoingMessage()
    {
        var handler = new GlobalExceptionHandler(NullLogger<GlobalExceptionHandler>.Instance);
        DefaultHttpContext context = CreateHttpContext();
        var exception = new BadHttpRequestException(
            "Unexpected end of request content.",
            StatusCodes.Status400BadRequest);

        bool handled = await handler.TryHandleAsync(context, exception, CancellationToken.None);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        ApiErrorResponse body = await ReadBodyAsync(context);
        Assert.Equal("The request could not be processed.", body.Error);
        Assert.DoesNotContain(exception.Message, body.Error);
    }

    [Fact]
    public async Task TryHandleAsync_MapsUnrelatedExceptionsToGeneric500()
    {
        var handler = new GlobalExceptionHandler(NullLogger<GlobalExceptionHandler>.Instance);
        DefaultHttpContext context = CreateHttpContext();
        var exception = new InvalidOperationException("internal detail that must not leak");

        bool handled = await handler.TryHandleAsync(context, exception, CancellationToken.None);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
        ApiErrorResponse body = await ReadBodyAsync(context);
        Assert.Equal("An unexpected server error occurred.", body.Error);
        Assert.DoesNotContain(exception.Message, body.Error);
    }

    private static async Task<ApiErrorResponse> ReadBodyAsync(HttpContext context)
    {
        context.Response.Body.Position = 0;
        ApiErrorResponse? body = await JsonSerializer.DeserializeAsync<ApiErrorResponse>(
            context.Response.Body,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return Assert.IsType<ApiErrorResponse>(body);
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
