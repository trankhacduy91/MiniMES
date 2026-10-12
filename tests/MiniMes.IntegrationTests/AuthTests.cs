using System.Net;
using System.Net.Http.Json;
using MiniMes.Contracts.Auth;
using MiniMes.IntegrationTests.Infrastructure;

namespace MiniMes.IntegrationTests;

[Collection(ApiCollection.Name)]
public class AuthTests(ApiFixture api)
{
    [Fact]
    public async Task Admin_login_returns_token_with_admin_role()
    {
        var response = await api.Anonymous().PostAsJsonAsync("/api/auth/login",
            new LoginRequest("admin", ApiFixture.DemoPassword));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<LoginResponse>();
        Assert.False(string.IsNullOrWhiteSpace(body!.Token));
        Assert.Equal(["Admin"], body.Roles);
        Assert.Null(body.StationCode);
    }

    [Fact]
    public async Task Wrong_password_is_401()
    {
        var response = await api.Anonymous().PostAsJsonAsync("/api/auth/login", new LoginRequest("admin", "wrong"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Admin_endpoint_rejects_operator_and_anonymous()
    {
        var asOperator = await (await api.LoginOperatorAsync()).GetAsync("/api/audit-logs");
        var anonymous = await api.Anonymous().GetAsync("/api/audit-logs");
        var asAdmin = await (await api.LoginAsync("admin")).GetAsync("/api/audit-logs");

        Assert.Equal(HttpStatusCode.Forbidden, asOperator.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.OK, asAdmin.StatusCode);
    }

    [Fact]
    public async Task Operator_token_carries_station_and_only_operator_role()
    {
        var response = await api.Anonymous().PostAsJsonAsync("/api/auth/login-operator",
            new OperatorLoginRequest("EMP00123", "L1-TEST-01"));

        var body = await response.Content.ReadFromJsonAsync<LoginResponse>();
        Assert.Equal("L1-TEST-01", body!.StationCode);
        Assert.Equal(["Operator"], body.Roles);
    }

    [Fact]
    public async Task Badge_login_cannot_open_a_password_account()
    {
        var response = await api.Anonymous().PostAsJsonAsync("/api/auth/login-operator",
            new OperatorLoginRequest("admin", "L1-TEST-01"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Operator_login_with_unknown_station_is_404()
    {
        var response = await api.Anonymous().PostAsJsonAsync("/api/auth/login-operator",
            new OperatorLoginRequest("EMP00123", "NOPE-01"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
