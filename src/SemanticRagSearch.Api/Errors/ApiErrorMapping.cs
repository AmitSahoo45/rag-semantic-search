using SemanticRagSearch.Api.Ollama;

namespace SemanticRagSearch.Api.Errors;

internal readonly record struct ApiError(int StatusCode, string Message);

internal static class ApiErrorMapping
{
    public static ApiError From(Exception exception)
    {
        return exception switch
        {
            OllamaProviderException
            {
                FailureKind: OllamaFailureKind.Unavailable
            } => new ApiError(
                StatusCodes.Status503ServiceUnavailable,
                "The AI provider is unavailable."),
            OllamaProviderException
            {
                FailureKind: OllamaFailureKind.Timeout
            } => new ApiError(
                StatusCodes.Status504GatewayTimeout,
                "The AI provider timed out."),
            OllamaProviderException => new ApiError(
                StatusCodes.Status502BadGateway,
                "The AI provider returned an invalid response."),
            BadHttpRequestException badRequest => new ApiError(
                badRequest.StatusCode,
                badRequest.StatusCode == StatusCodes.Status413PayloadTooLarge
                    ? "Request body is too large."
                    : "The request could not be processed."),
            _ => new ApiError(
                StatusCodes.Status500InternalServerError,
                "An unexpected server error occurred.")
        };
    }

    public static void Log(
        ILogger logger,
        Exception exception,
        HttpContext httpContext)
    {
        if (exception is OllamaProviderException providerException)
        {
            logger.LogWarning(
                providerException,
                "Ollama provider failure {FailureKind} with upstream status {UpstreamStatusCode}.",
                providerException.FailureKind,
                providerException.UpstreamStatusCode);
            return;
        }

        if (exception is BadHttpRequestException badRequest)
        {
            logger.LogWarning(
                badRequest,
                "Rejected request {RequestMethod} {RequestPath} with status {StatusCode}.",
                httpContext.Request.Method,
                httpContext.Request.Path,
                badRequest.StatusCode);
            return;
        }

        logger.LogError(
            exception,
            "Unhandled exception while processing {RequestMethod} {RequestPath}.",
            httpContext.Request.Method,
            httpContext.Request.Path);
    }
}
