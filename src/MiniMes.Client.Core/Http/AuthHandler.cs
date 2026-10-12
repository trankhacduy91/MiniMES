using System.Net.Http.Headers;
using MiniMes.Client.Core.Auth;

namespace MiniMes.Client.Core.Http;

// Runs for every request of ApiClient: adds the JWT and a correlation id
// (the API logs the same id, so a user's "ref: ..." finds the server log lines).
public sealed class AuthHandler(ITokenStore tokens) : DelegatingHandler
{
    public const string CorrelationHeader = "X-Correlation-Id";

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (tokens.AccessToken is { } token)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        if (!request.Headers.Contains(CorrelationHeader))
            request.Headers.TryAddWithoutValidation(CorrelationHeader, Guid.NewGuid().ToString("N"));

        return base.SendAsync(request, ct);
    }
}
