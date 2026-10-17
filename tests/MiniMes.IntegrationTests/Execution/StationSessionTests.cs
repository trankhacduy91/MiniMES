using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using MiniMes.Contracts.Execution;
using MiniMes.Contracts.Planning;
using MiniMes.IntegrationTests.Infrastructure;
using MiniMes.IntegrationTests.Planning;

namespace MiniMes.IntegrationTests.Execution;

// Spec M3 step 5: POST /api/stations/{id}/sessions (open), DELETE /api/stations/{id}/sessions/{sessionId} (close).
// Uses L1-ASM-01 so the other execution tests' stations are not disturbed more than they already are.
[Collection(ApiCollection.Name)]
public class StationSessionTests(ApiFixture api)
{
    private const string Station = "L1-ASM-01";
    private static readonly DateTime Start = new(2026, 11, 2, 1, 0, 0, DateTimeKind.Utc);
    private readonly ScanTests scan = new(api);

    [Fact]
    public async Task Opening_a_session_records_who_and_closes_the_previous_one()
    {
        var (wo, _) = await scan.ReleasedAsync(1);
        var stationId = await scan.StationIdAsync(Station);

        var first = await OpenAsync(await api.LoginOperatorAsync(), stationId, wo.Id);
        var second = await OpenAsync(await api.LoginAsync("admin"), stationId, wo.Id);   // next shift logs in

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var a = (await first.Content.ReadFromJsonAsync<StationSessionDto>())!;
        var b = (await second.Content.ReadFromJsonAsync<StationSessionDto>())!;
        Assert.Equal((Station, wo.WoNo, "EMP00123", (DateTime?)null), (a.StationCode, a.WoNo, a.OperatorCode, a.ClosedAt));
        Assert.Equal("admin", b.OperatorCode);
        var sessions = await api.WithDbAsync(db => db.StationSessions.Where(s => s.Id == a.Id || s.Id == b.Id).OrderBy(s => s.Id).ToListAsync());
        Assert.Equal([false, true], sessions.Select(s => s.IsOpen));
        Assert.Equal(1, await api.WithDbAsync(db => db.StationSessions.CountAsync(s => s.StationId == stationId && s.ClosedAt == null)));
    }

    [Fact]
    public async Task Scan_is_refused_after_logout()
    {
        var (wo, serials) = await scan.ReleasedAsync(1);
        var op = await api.LoginOperatorAsync();
        var stationId = await scan.StationIdAsync("L1-SMT-01");
        var session = await scan.OpenSessionAsync("L1-SMT-01", wo.Id);

        var logout = await op.DeleteAsync($"/api/stations/{stationId}/sessions/{session.Id}");
        var answer = await scan.ScanAsync(op, "L1-SMT-01", serials[0]);
        var again = await op.DeleteAsync($"/api/stations/{stationId}/sessions/{session.Id}");
        var otherStation = await op.DeleteAsync($"/api/stations/{await scan.StationIdAsync(Station)}/sessions/{session.Id}");

        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        Assert.Equal(("Blocked", "NoOpenSession"), (answer.Result, answer.ReasonCode));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, again.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, otherStation.StatusCode);
    }

    [Fact]
    public async Task A_station_only_runs_an_order_of_its_line_that_is_on_the_line()
    {
        var planner = await api.LoginAsync("planner01");
        var op = await api.LoginOperatorAsync();
        var stationId = await scan.StationIdAsync(Station);
        var pcbA = await api.WithDbAsync(db => db.Items.Where(i => i.Code == "PCB-A").Select(i => i.Id).SingleAsync());
        var line2 = await api.WithDbAsync(db => db.Lines.Where(l => l.Code == "L2").Select(l => l.Id).SingleAsync());
        var draft = await WorkOrderApiTests.CreateAsync(planner, new CreateWorkOrderRequest(pcbA, 1, 1, Start, Start.AddHours(4)));
        var otherLine = await WorkOrderApiTests.CreateAsync(planner, new CreateWorkOrderRequest(pcbA, line2, 1, Start, Start.AddHours(4)));
        var (released, _) = await scan.ReleasedAsync(1);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await OpenAsync(op, stationId, draft.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await OpenAsync(op, stationId, otherLine.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await OpenAsync(op, stationId, int.MaxValue)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await OpenAsync(op, int.MaxValue, released.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await OpenAsync(op, stationId, 0)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await OpenAsync(planner, stationId, released.Id)).StatusCode);
    }

    // Two kiosks log in at the same station at the same instant: whatever the interleaving, one session is open.
    [Fact]
    public async Task Simultaneous_logins_leave_one_open_session()
    {
        var (wo, _) = await scan.ReleasedAsync(1);
        var stationId = await scan.StationIdAsync(Station);
        var op = await api.LoginOperatorAsync();
        var admin = await api.LoginAsync("admin");

        for (var round = 0; round < 5; round++)
        {
            var answers = await ScanTests.TogetherAsync(() => OpenAsync(op, stationId, wo.Id), () => OpenAsync(admin, stationId, wo.Id));

            Assert.Contains(answers, a => a.StatusCode == HttpStatusCode.Created);
            Assert.All(answers, a => Assert.True(a.StatusCode is HttpStatusCode.Created or HttpStatusCode.Conflict, a.StatusCode.ToString()));
            Assert.Equal(1, await api.WithDbAsync(db => db.StationSessions.CountAsync(s => s.StationId == stationId && s.ClosedAt == null)));
        }
    }

    private static Task<HttpResponseMessage> OpenAsync(HttpClient client, int stationId, int woId)
        => client.PostAsJsonAsync($"/api/stations/{stationId}/sessions", new OpenSessionRequest(woId));
}
