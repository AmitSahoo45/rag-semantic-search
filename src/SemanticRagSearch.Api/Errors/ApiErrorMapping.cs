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

        logger.LogError(
            exception,
            "Unhandled exception while processing {RequestMethod} {RequestPath}.",
            httpContext.Request.Method,
            httpContext.Request.Path);
    }
}
