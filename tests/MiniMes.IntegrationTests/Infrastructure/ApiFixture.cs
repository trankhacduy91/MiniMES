using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using MiniMes.Contracts.Auth;
using MiniMes.Infrastructure.Persistence;

namespace MiniMes.IntegrationTests.Infrastructure;

// Container + API host, created once and shared by every test class in the "Api" collection.
// Tests share one DB, so each test uses its own codes (Unique()) instead of relying on run order.
public sealed class ApiFixture : IAsyncLifetime
{
    public const string DemoPassword = DevSeeder.DemoPassword;

    private readonly SqlServerFixture _sql = new();
    public ApiFactory Factory { get; private set; } = default!;

    public async Task InitializeAsync()
    {
        await _sql.InitializeAsync();
        Factory = new ApiFactory(_sql);
        _ = Factory.Services; // start the host now: runs migrations + seed once
    }

    public async Task DisposeAsync()
    {
        await Factory.DisposeAsync();
        await _sql.DisposeAsync();
    }

    public HttpClient Anonymous() => Factory.CreateClient();

    public async Task<HttpClient> LoginAsync(string employeeCode, string password = DemoPassword)
    {
        var client = Factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(employeeCode, password));
        response.EnsureSuccessStatusCode();
        return WithToken(client, (await response.Content.ReadFromJsonAsync<LoginResponse>())!.Token);
    }

    public async Task<HttpClient> LoginOperatorAsync(string employeeCode = "EMP00123", string stationCode = "L1-TEST-01")
    {
        var client = Factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/login-operator", new OperatorLoginRequest(employeeCode, stationCode));
        response.EnsureSuccessStatusCode();
        return WithToken(client, (await response.Content.ReadFromJsonAsync<LoginResponse>())!.Token);
    }

    // Direct DB access for assertions and for things the API deliberately cannot do (e.g. UPDATE AuditLog).
    public async Task<T> WithDbAsync<T>(Func<AppDbContext, Task<T>> action)
    {
        using var scope = Factory.Services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    public static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}";

    private static HttpClient WithToken(HttpClient client, string token)
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}

[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<ApiFixture>
{
    public const string Name = "Api";
}
