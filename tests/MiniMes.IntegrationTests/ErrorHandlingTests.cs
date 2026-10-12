using System.Net;
using System.Net.Http.Json;
using MiniMes.Contracts.Factory;
using MiniMes.IntegrationTests.Infrastructure;

namespace MiniMes.IntegrationTests;

// The WPF ApiClient (step 10) relies on one error format: ProblemDetails, application/problem+json.
[Collection(ApiCollection.Name)]
public class ErrorHandlingTests(ApiFixture api)
{
    [Fact]
    public async Task Not_found_is_problem_json_with_correlation_id()
    {
        var admin = await api.LoginAsync("admin");
        admin.DefaultRequestHeaders.Add("X-Correlation-Id", "test-corr-1");

        var response = await admin.GetAsync("/api/stations/999999");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("test-corr-1", response.Headers.GetValues("X-Correlation-Id").Single());
        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        Assert.Equal("test-corr-1", body!["correlationId"].ToString());
    }

    [Fact]
    public async Task Conflict_is_problem_json()
    {
        var admin = await api.LoginAsync("admin");

        var response = await admin.PostAsJsonAsync("/api/stations",
            new CreateStationRequest { LineId = 1, Code = "L1-SMT-01", Name = "dup", Type = "Smt" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Business_rule_violation_is_422_problem_json()
    {
        var admin = await api.LoginAsync("admin");
        var line = await CreateInactiveLineAsync(admin);

        var response = await admin.PostAsJsonAsync("/api/stations",
            new CreateStationRequest { LineId = line.Id, Code = ApiFixture.Unique("X"), Name = "x", Type = "Smt" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Validation_error_is_problem_json()
    {
        var admin = await api.LoginAsync("admin");

        var response = await admin.PostAsJsonAsync("/api/sites", new CreateSiteRequest { Code = "bad code", Name = "x" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    private static async Task<LineDto> CreateInactiveLineAsync(HttpClient admin)
    {
        var created = await admin.PostAsJsonAsync("/api/lines",
            new CreateLineRequest { AreaId = 1, Code = ApiFixture.Unique("LX"), Name = "Inactive line" });
        created.EnsureSuccessStatusCode();
        var line = (await created.Content.ReadFromJsonAsync<LineDto>())!;

        var put = await admin.PutAsJsonAsync($"/api/lines/{line.Id}", new UpdateLineRequest { Name = line.Name, IsActive = false });
        put.EnsureSuccessStatusCode();
        return line;
    }
}
