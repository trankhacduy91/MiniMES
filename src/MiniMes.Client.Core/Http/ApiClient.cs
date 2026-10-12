using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MiniMes.Client.Core.Resources;
using MiniMes.Contracts.Common;

namespace MiniMes.Client.Core.Http;

// Typed HttpClient (IHttpClientFactory). One place turns HTTP results into either a DTO or an
// ApiException with a user-facing message, so ViewModels never parse HTTP themselves.
// Relative URLs have no leading slash ("api/auth/login") so they append to BaseAddress.
public sealed class ApiClient(HttpClient http)
{
    public Task<T> GetAsync<T>(string url, CancellationToken ct = default)
        => SendAsync<T>(new HttpRequestMessage(HttpMethod.Get, url), ct);

    public Task<TResponse> PostAsync<TRequest, TResponse>(string url, TRequest body, CancellationToken ct = default)
        => SendAsync<TResponse>(new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) }, ct);

    public Task<TResponse> PutAsync<TRequest, TResponse>(string url, TRequest body, CancellationToken ct = default)
        => SendAsync<TResponse>(new HttpRequestMessage(HttpMethod.Put, url) { Content = JsonContent.Create(body) }, ct);

    private async Task<T> SendAsync<T>(HttpRequestMessage request, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, ct);
        }
        // No response at all: network down, DNS, server stopped, or HttpClient.Timeout.
        catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            throw new ApiException(null, Strings.Error_CannotReachServer);
        }

        using (response)
        {
            if (response.IsSuccessStatusCode)
                return (await response.Content.ReadFromJsonAsync<T>(ct))!;

            throw await ToApiExceptionAsync(response, ct);
        }
    }

    private static async Task<ApiException> ToApiExceptionAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var problem = await TryReadProblemAsync(response, ct);
        var correlationId = problem?.CorrelationId
            ?? (response.Headers.TryGetValues(AuthHandler.CorrelationHeader, out var ids) ? ids.FirstOrDefault() : null);

        var message = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => Strings.Error_SessionExpired, // login screens map 401 themselves
            HttpStatusCode.Forbidden => Strings.Error_Forbidden,
            HttpStatusCode.BadRequest when problem?.Errors is { Count: > 0 } errors
                => string.Join(Environment.NewLine, errors.SelectMany(e => e.Value)),
            _ => problem?.Detail ?? problem?.Title ?? Strings.Error_Unexpected,
        };

        // Server errors: give the user a reference IT can grep in the API log.
        if ((int)response.StatusCode >= 500 && correlationId is not null)
            message = string.Format(Strings.Error_WithReference, message, correlationId);

        return new ApiException(response.StatusCode, message, correlationId);
    }

    private static async Task<ProblemDto?> TryReadProblemAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<ProblemDto>(ct);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return null; // empty body (e.g. plain 401 from JwtBearer) or not JSON
        }
    }
}
