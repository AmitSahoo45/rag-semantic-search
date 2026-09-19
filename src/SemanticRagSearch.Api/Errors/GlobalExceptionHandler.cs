using Microsoft.AspNetCore.Diagnostics;
namespace SemanticRagSearch.Api.Errors;

public sealed class GlobalExceptionHandler(
    ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is OperationCanceledException &&
            httpContext.RequestAborted.IsCancellationRequested)
        {
            return false;
        }

        if (httpContext.Response.HasStarted)
            return false;

        ApiError error = ApiErrorMapping.From(exception);
        ApiErrorMapping.Log(logger, exception, httpContext);

        httpContext.Response.StatusCode = error.StatusCode;
        await httpContext.Response.WriteAsJsonAsync(
            new ApiErrorResponse(error.Message),
            cancellationToken);
        return true;
    }
}
