using System.Net;
using System.Net.Http.Json;
using System.Text;
using MiniMes.Client.Core.Auth;
using MiniMes.Client.Core.Http;
using MiniMes.Client.Core.Resources;
using MiniMes.Contracts.Auth;

namespace MiniMes.UnitTests.Client;

public class ApiClientTests
{
    // Replaces the network: returns a canned response (or throws) and records what was sent.
    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            return Task.FromResult(respond(request));
        }
    }

    private static (ApiClient Api, FakeHandler Handler, TokenStore Tokens) Create(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var tokens = new TokenStore();
        var fake = new FakeHandler(respond);
        var http = new HttpClient(new AuthHandler(tokens) { InnerHandler = fake }) { BaseAddress = new Uri("http://api.test/") };
        return (new ApiClient(http), fake, tokens);
    }

    private static HttpResponseMessage Problem(HttpStatusCode status, string json) => new(status)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/problem+json"),
    };

    [Fact]
    public async Task Success_returns_the_dto()
    {
        var (api, _, _) = Create(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new LoginResponse("t", DateTime.UtcNow, "admin", "Admin", ["Admin"], null)),
        });

        var result = await api.GetAsync<LoginResponse>("api/x");

        Assert.Equal("admin", result.EmployeeCode);
    }

    [Fact]
    public async Task Token_and_correlation_id_are_added_to_every_request()
    {
        var (api, handler, tokens) = Create(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(1) });
        tokens.Set(new LoginResponse("abc.def.ghi", DateTime.UtcNow, "admin", "Admin", ["Admin"], null));

        await api.GetAsync<int>("api/x");

        Assert.Equal("Bearer", handler.LastRequest!.Headers.Authorization!.Scheme);
        Assert.Equal("abc.def.ghi", handler.LastRequest.Headers.Authorization.Parameter);
        Assert.True(handler.LastRequest.Headers.Contains(AuthHandler.CorrelationHeader));
        Assert.Equal("http://api.test/api/x", handler.LastRequest.RequestUri!.ToString());
    }

    [Fact]
    public async Task No_token_means_no_authorization_header()
    {
        var (api, handler, _) = Create(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(1) });

        await api.GetAsync<int>("api/x");

        Assert.Null(handler.LastRequest!.Headers.Authorization);
    }

    [Fact]
    public async Task Problem_details_become_api_exception_with_detail_and_status()
    {
        var (api, _, _) = Create(_ => Problem(HttpStatusCode.Conflict,
            """{"title":"Conflict","status":409,"detail":"Station 'L1-SMT-01' already exists.","correlationId":"abc"}"""));

        var ex = await Assert.ThrowsAsync<ApiException>(() => api.GetAsync<int>("api/x"));

        Assert.Equal(HttpStatusCode.Conflict, ex.Status);
        Assert.Equal("Station 'L1-SMT-01' already exists.", ex.Message);
        Assert.Equal("abc", ex.CorrelationId);
    }

    [Fact]
    public async Task Server_error_message_carries_a_reference_for_it()
    {
        var (api, _, _) = Create(_ => Problem(HttpStatusCode.InternalServerError,
            """{"title":"Unexpected error","status":500,"detail":"An unexpected error occurred.","correlationId":"ref123"}"""));

        var ex = await Assert.ThrowsAsync<ApiException>(() => api.GetAsync<int>("api/x"));

        Assert.Equal(string.Format(Strings.Error_WithReference, "An unexpected error occurred.", "ref123"), ex.Message);
    }

    [Fact]
    public async Task Validation_errors_are_joined_into_the_message()
    {
        var (api, _, _) = Create(_ => Problem(HttpStatusCode.BadRequest,
            """{"title":"One or more validation errors occurred.","status":400,"errors":{"Code":["Code must contain only A-Z, 0-9 or '-'."]}}"""));

        var ex = await Assert.ThrowsAsync<ApiException>(() => api.GetAsync<int>("api/x"));

        Assert.Equal("Code must contain only A-Z, 0-9 or '-'.", ex.Message);
    }

    // JwtBearer answers 401 with an empty body: must not crash while reading ProblemDetails.
    [Fact]
    public async Task Unauthorized_without_body_maps_to_session_expired()
    {
        var (api, _, _) = Create(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));

        var ex = await Assert.ThrowsAsync<ApiException>(() => api.GetAsync<int>("api/x"));

        Assert.Equal(HttpStatusCode.Unauthorized, ex.Status);
        Assert.Equal(Strings.Error_SessionExpired, ex.Message);
    }

    [Fact]
    public async Task Network_failure_is_cannot_reach_server()
    {
        var (api, _, _) = Create(_ => throw new HttpRequestException("No connection could be made"));

        var ex = await Assert.ThrowsAsync<ApiException>(() => api.GetAsync<int>("api/x"));

        Assert.True(ex.IsNetworkError);
        Assert.Equal(Strings.Error_CannotReachServer, ex.Message);
    }

    [Fact]
    public async Task Timeout_is_cannot_reach_server()
    {
        var (api, _, _) = Create(_ => throw new TaskCanceledException("HttpClient.Timeout"));

        var ex = await Assert.ThrowsAsync<ApiException>(() => api.GetAsync<int>("api/x"));

        Assert.True(ex.IsNetworkError);
    }

    [Fact]
    public async Task Cancellation_by_the_caller_is_not_reported_as_network_error()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var (api, _, _) = Create(_ => throw new TaskCanceledException());

        await Assert.ThrowsAsync<TaskCanceledException>(() => api.GetAsync<int>("api/x", cts.Token));
    }
}
