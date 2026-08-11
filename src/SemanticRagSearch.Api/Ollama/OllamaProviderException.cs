using System.Net;

namespace SemanticRagSearch.Api.Ollama;

public sealed class OllamaProviderException : Exception
{
    public OllamaProviderException(
        OllamaFailureKind failureKind,
        string message,
        HttpStatusCode? upstreamStatusCode = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        FailureKind = failureKind;
        UpstreamStatusCode = upstreamStatusCode;
    }

    public OllamaFailureKind FailureKind { get; }

    public HttpStatusCode? UpstreamStatusCode { get; }
}
