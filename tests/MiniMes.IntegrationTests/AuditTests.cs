using System.Net;
using System.Net.Http.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using MiniMes.Contracts.Audit;
using MiniMes.Contracts.Common;
using MiniMes.Contracts.Factory;
using MiniMes.IntegrationTests.Infrastructure;

namespace MiniMes.IntegrationTests;

[Collection(ApiCollection.Name)]
public class AuditTests(ApiFixture api)
{
    [Fact]
    public async Task Renaming_a_station_writes_one_audit_row_with_old_new_user_and_utc()
    {
        var admin = await api.LoginAsync("admin");
        var station = await CreateStationAsync(admin, "Old name");
        var before = DateTime.UtcNow;

        var put = await admin.PutAsJsonAsync($"/api/stations/{station.Id}",
            new UpdateStationRequest { Name = "New name", Type = "Smt", IsActive = true });
        put.EnsureSuccessStatusCode();

        var rows = (await AuditOfAsync(admin, station.Id)).Where(r => r.ColumnName != "*").ToList();
        var row = Assert.Single(rows);
        Assert.Equal("Name", row.ColumnName);
        Assert.Equal("Old name", row.OldValue);
        Assert.Equal("New name", row.NewValue);
        Assert.Equal("admin", row.ChangedBy);
        // Stored as UTC: within a few seconds of UtcNow, never shifted by the +07:00 factory offset.
        Assert.InRange(row.ChangedAt, before.AddSeconds(-5), DateTime.UtcNow.AddSeconds(5));
    }

    [Fact]
    public async Task Saving_the_same_value_writes_no_audit_row()
    {
        var admin = await api.LoginAsync("admin");
        var station = await CreateStationAsync(admin, "Same");

        await admin.PutAsJsonAsync($"/api/stations/{station.Id}",
            new UpdateStationRequest { Name = "Same", Type = "Smt", IsActive = true });

        Assert.DoesNotContain(await AuditOfAsync(admin, station.Id), r => r.ColumnName != "*");
    }

    [Fact]
    public async Task Creating_a_station_writes_one_json_row_with_the_real_id()
    {
        var admin = await api.LoginAsync("admin");
        var station = await CreateStationAsync(admin, "Created");

        var row = Assert.Single(await AuditOfAsync(admin, station.Id));
        Assert.Equal("*", row.ColumnName);
        Assert.Equal(station.Id.ToString(), row.RecordKey);
        Assert.Contains($"\"Code\":\"{station.Code}\"", row.NewValue);
        Assert.Null(row.OldValue);
    }

    // Data and audit must succeed or fail together. The Added row is written by a second
    // SaveChanges; make that one fail and the station itself must be rolled back.
    [Fact]
    public async Task Failure_writing_the_audit_row_rolls_back_the_new_station()
    {
        var admin = await api.LoginAsync("admin");
        var code = ApiFixture.Unique("L1-RB");
        await api.WithDbAsync(db => db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE dbo.AuditLog WITH NOCHECK ADD CONSTRAINT CK_Test_FailAddedStation " +
            "CHECK (NOT (TableName = N'Stations' AND ColumnName = N'*'))"));
        try
        {
            var response = await admin.PostAsJsonAsync("/api/stations",
                new CreateStationRequest { LineId = 1, Code = code, Name = "Rollback", Type = "Smt" });

            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            Assert.False(await api.WithDbAsync(db => db.Stations.AnyAsync(s => s.Code == code)));
        }
        finally
        {
            await api.WithDbAsync(db => db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE dbo.AuditLog DROP CONSTRAINT CK_Test_FailAddedStation"));
        }
    }

    // [v2] The trigger blocks UPDATE/DELETE at DB level, even for sa (the container login).
    [Theory]
    [InlineData("UPDATE dbo.AuditLog SET NewValue = N'tampered'")]
    [InlineData("DELETE FROM dbo.AuditLog")]
    public async Task AuditLog_cannot_be_updated_or_deleted(string sql)
    {
        var admin = await api.LoginAsync("admin");
        await CreateStationAsync(admin, "Ensure rows exist");
        var countBefore = await api.WithDbAsync(db => db.AuditLogs.CountAsync());

        var ex = await Assert.ThrowsAsync<SqlException>(() => api.WithDbAsync(db => db.Database.ExecuteSqlRawAsync(sql)));

        Assert.Equal(51000, ex.Number);
        Assert.Equal(countBefore, await api.WithDbAsync(db => db.AuditLogs.CountAsync()));
    }

    [Fact]
    public async Task Seeder_on_empty_db_is_audited_as_system_and_password_hash_is_masked()
    {
        var admin = await api.LoginAsync("admin");

        var sites = await admin.GetFromJsonAsync<PagedResult<AuditLogDto>>("/api/audit-logs?tableName=Sites");
        var seededSite = Assert.Single(sites!.Items, r => r.ColumnName == "*" && r.NewValue!.Contains("\"VN-BN01\""));
        Assert.Equal("system", seededSite.ChangedBy);

        var users = await admin.GetFromJsonAsync<PagedResult<AuditLogDto>>("/api/audit-logs?tableName=Users&pageSize=200");
        var adminRow = Assert.Single(users!.Items, r => r.NewValue!.Contains("\"EmployeeCode\":\"admin\""));
        Assert.Contains("\"PasswordHash\":\"***\"", adminRow.NewValue);
        Assert.DoesNotContain("AQAAAA", adminRow.NewValue); // prefix of an ASP.NET Core Identity hash
    }

    private static async Task<StationDto> CreateStationAsync(HttpClient admin, string name)
    {
        var response = await admin.PostAsJsonAsync("/api/stations",
            new CreateStationRequest { LineId = 1, Code = ApiFixture.Unique("L1-T"), Name = name, Type = "Smt" });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<StationDto>())!;
    }

    private static async Task<IReadOnlyList<AuditLogDto>> AuditOfAsync(HttpClient admin, int stationId)
        => (await admin.GetFromJsonAsync<PagedResult<AuditLogDto>>(
            $"/api/audit-logs?tableName=Stations&recordKey={stationId}"))!.Items;
}
