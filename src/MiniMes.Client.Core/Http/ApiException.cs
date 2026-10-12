using System.Net;

namespace MiniMes.Client.Core.Http;

// Every failed API call ends as this exception, with a message that is safe to show to the user.
// Status is null when no HTTP response came back (network down, timeout).
public sealed class ApiException(HttpStatusCode? status, string message, string? correlationId = null)
    : Exception(message)
{
    public HttpStatusCode? Status { get; } = status;
    public string? CorrelationId { get; } = correlationId;
    public bool IsNetworkError => Status is null;
}
