using System.Net;
using System.Net.Http.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using MiniMes.Contracts.Factory;
using MiniMes.IntegrationTests.Infrastructure;

namespace MiniMes.IntegrationTests;

[Collection(ApiCollection.Name)]
public class FactoryTests(ApiFixture api)
{
    private static readonly string[] SeedStations =
    [
        "L1-SMT-01", "L1-ASM-01", "L1-TEST-01", "L1-PACK-01", "L1-REPAIR",
        "L2-SMT-01", "L2-ASM-01", "L2-TEST-01", "L2-PACK-01", "L2-REPAIR",
    ];

    [Fact]
    public async Task Seed_has_one_site_one_area_two_lines_and_ten_stations()
    {
        var reader = await api.LoginOperatorAsync(); // any logged-in role can read

        var sites = await reader.GetFromJsonAsync<List<SiteDto>>("/api/sites");
        var areas = await reader.GetFromJsonAsync<List<AreaDto>>("/api/areas");
        var lines = await reader.GetFromJsonAsync<List<LineDto>>("/api/lines");
        var stations = await reader.GetFromJsonAsync<List<StationDto>>("/api/stations");

        Assert.Contains(sites!, s => s.Code == "VN-BN01");
        Assert.Contains(areas!, a => a.Code == "SMT-ASSY");
        Assert.Superset(new HashSet<string> { "L1", "L2" }, lines!.Select(l => l.Code).ToHashSet());
        // Other tests add stations to the shared DB, so check the seed ones are all there.
        Assert.Superset(SeedStations.ToHashSet(), stations!.Select(s => s.Code).ToHashSet());
    }

    [Theory]
    [InlineData("L1 SMT")]   // space
    [InlineData("l1-smt")]   // lowercase
    [InlineData("L1/SMT")]   // MQTT topic separator
    public async Task Invalid_code_is_400(string code)
    {
        var admin = await api.LoginAsync("admin");

        var response = await admin.PostAsJsonAsync("/api/stations",
            new CreateStationRequest { LineId = 1, Code = code, Name = "x", Type = "Smt" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Duplicate_station_code_is_409()
    {
        var admin = await api.LoginAsync("admin");

        var response = await admin.PostAsJsonAsync("/api/stations",
            new CreateStationRequest { LineId = 1, Code = "L1-SMT-01", Name = "dup", Type = "Smt" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Operator_cannot_change_factory_configuration()
    {
        var op = await api.LoginOperatorAsync();

        var response = await op.PutAsJsonAsync("/api/stations/1",
            new UpdateStationRequest { Name = "x", Type = "Smt", IsActive = true });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task There_is_no_delete_endpoint()
    {
        var admin = await api.LoginAsync("admin");

        var response = await admin.DeleteAsync("/api/stations/1");

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    // The unique indexes are the real guard (the API pre-check can lose a race), so test them directly.
    [Theory]
    [InlineData("INSERT INTO Stations (LineId, Code, Name, Type, IsActive, CreatedAt, CreatedBy) VALUES (1, 'L1-SMT-01', 'dup', 'Smt', 1, SYSUTCDATETIME(), 'test')")]
    [InlineData("INSERT INTO Lines (AreaId, Code, Name, IsActive, CreatedAt, CreatedBy) VALUES (1, 'L1', 'dup', 1, SYSUTCDATETIME(), 'test')")]
    public async Task Unique_index_rejects_duplicates_at_db_level(string sql)
    {
        var ex = await Assert.ThrowsAsync<SqlException>(() => api.WithDbAsync(db => db.Database.ExecuteSqlRawAsync(sql)));

        Assert.Equal(2601, ex.Number); // duplicate key in unique index
    }
}
