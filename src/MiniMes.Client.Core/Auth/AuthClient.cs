using MiniMes.Client.Core.Http;
using MiniMes.Contracts.Auth;

namespace MiniMes.Client.Core.Auth;

public interface IAuthClient
{
    Task<LoginResponse> LoginAsync(string employeeCode, string password, CancellationToken ct = default);
    Task<LoginResponse> LoginOperatorAsync(string employeeCode, string stationCode, CancellationToken ct = default);
    void Logout();
}

public sealed class AuthClient(ApiClient api, ITokenStore tokens) : IAuthClient
{
    public async Task<LoginResponse> LoginAsync(string employeeCode, string password, CancellationToken ct = default)
    {
        var session = await api.PostAsync<LoginRequest, LoginResponse>(
            "api/auth/login", new LoginRequest(employeeCode, password), ct);
        tokens.Set(session);
        return session;
    }

    public async Task<LoginResponse> LoginOperatorAsync(string employeeCode, string stationCode, CancellationToken ct = default)
    {
        var session = await api.PostAsync<OperatorLoginRequest, LoginResponse>(
            "api/auth/login-operator", new OperatorLoginRequest(employeeCode, stationCode), ct);
        tokens.Set(session);
        return session;
    }

    // JWTs cannot be revoked server-side; logging out = forgetting the token on this PC.
    public void Logout() => tokens.Clear();
}
