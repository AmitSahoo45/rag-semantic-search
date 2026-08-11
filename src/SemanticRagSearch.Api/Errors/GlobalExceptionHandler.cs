using Microsoft.AspNetCore.Diagnostics;
using SemanticRagSearch.Api.Ollama;

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

        int statusCode;
        string error;

        if (exception is OllamaProviderException providerException)
        {
            (statusCode, error) = providerException.FailureKind switch
            {
                OllamaFailureKind.InvalidResponse =>
                    (StatusCodes.Status502BadGateway,
                        "The AI provider returned an invalid response."),
                OllamaFailureKind.Unavailable =>
                    (StatusCodes.Status503ServiceUnavailable,
                        "The AI provider is unavailable."),
                OllamaFailureKind.Timeout =>
                    (StatusCodes.Status504GatewayTimeout,
                        "The AI provider timed out."),
                _ =>
                    (StatusCodes.Status502BadGateway,
                        "The AI provider returned an invalid response.")
            };

            logger.LogWarning(
                providerException,
                "Ollama provider failure {FailureKind} with upstream status {UpstreamStatusCode}.",
                providerException.FailureKind,
                providerException.UpstreamStatusCode);
        }
        else
        {
            statusCode = StatusCodes.Status500InternalServerError;
            error = "An unexpected server error occurred.";
            logger.LogError(
                exception,
                "Unhandled exception while processing {RequestMethod} {RequestPath}.",
                httpContext.Request.Method,
                httpContext.Request.Path);
        }

        httpContext.Response.StatusCode = statusCode;
        await httpContext.Response.WriteAsJsonAsync(
            new ApiErrorResponse(error),
            cancellationToken);
        return true;
    }
}
