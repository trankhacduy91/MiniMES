using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using MiniMes.Application.Common;
using MiniMes.Application.Oee;
using MiniMes.Contracts.Oee;
using MiniMes.Domain.Machines;
using MiniMes.Infrastructure.Oee;
using MiniMes.IntegrationTests.Infrastructure;
using static MiniMes.IntegrationTests.Oee.OeeTestData;

namespace MiniMes.IntegrationTests.Oee;

// Spec M5 step 7 over HTTP: history, Pareto, the to-classify list and reason change (roles + audit), the Excel files,
// the current shift. The API runs on the real clock, so the data sits on days of September 2026 (already past).
[Collection(ApiCollection.Name)]
public class OeeApiTests(ApiFixture api)
{
    private static readonly PlantTimeZone Plant = new(TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh"));
    private readonly RecordingNotifier Notifier = new();

    private Task<int> CloseAsync(OeeTestData.Plant plant, DateTime now) => api.WithDbAsync(db =>
    {
        var time = new FakeTimeProvider(new DateTimeOffset(now));
        var options = Options.Create(new OeeOptions());
        return new OeeSnapshotService(db, new OeeInputReader(db, time, options), Plant, time, options, Notifier,
            NullLogger<OeeSnapshotService>.Instance).CloseDueBucketsAsync(CancellationToken.None, [plant.MachineId]);
    });

    private Task<string> CodeAsync(OeeTestData.Plant plant)
        => api.WithDbAsync(db => db.Machines.Where(m => m.Id == plant.MachineId).Select(m => m.Code).SingleAsync());

    private static string D(Func<int, int, int, DateTime> at) => Plant.DateOf(at(8, 0, 0)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    [Fact]
    public async Task History_and_pareto_of_the_sample_shift()
    {
        var at = PastDay(15);   // Tuesday
        var plant = await SampleShiftAsync(api, at);
        var code = await CodeAsync(plant);
        await CloseAsync(plant, at(14, 5, 0));
        var client = await api.LoginOperatorAsync();   // every role reads

        Assert.Contains(Notifier.Pushed.OfType<OeeSnapshotClosedDto>(),
            p => p.Machine == code && p.Shift == "S1" && p.Line == "L1" && p.Hours.Contains(null) && p.Hours.Count == 9);

        var history = await client.GetFromJsonAsync<List<OeeSnapshotDto>>($"/api/oee/history?from={D(at)}&to={D(at)}&machine={code}");
        var s1 = Assert.Single(history!, h => h.Shift == "S1");
        Assert.Equal((null, 405 * 60, 360 * 60, 1000L, 970L), (s1.Hour, s1.Figures.PlannedSeconds, s1.Figures.RunSeconds,
            s1.Figures.TotalCount, s1.Figures.GoodCount));
        Assert.Equal(0.7984, s1.Figures.Oee!.Value, 4);
        Assert.Empty(s1.Figures.Warnings);
        var hourly = await client.GetFromJsonAsync<List<OeeSnapshotDto>>($"/api/oee/history?from={D(at)}&to={D(at)}&machine={code}&hourly=true");
        Assert.Equal(9, hourly!.Count(h => h.Shift == "S1"));

        // The Pareto adds up exactly to what Availability lost: 405 - 360 = 45 minutes (micro stops and breaks out).
        var pareto = await client.GetFromJsonAsync<ParetoDto>($"/api/downtime/pareto?from={D(at)}&to={D(at)}&machine={code}&shift=S1");
        Assert.Equal([("FDR-JAM", 25.0, 1), ("MAT-WAIT", 12.0, 1), ("(Down)", 8.0, 1)],
            pareto!.Rows.Select(r => (r.Reason, r.Minutes, r.Count)));
        Assert.Equal((s1.Figures.PlannedSeconds - s1.Figures.RunSeconds) / 60.0, pareto.TotalMinutes);
        Assert.Equal((55.56, 82.22, 100.0), (pareto.Rows[0].Percent, pareto.Rows[1].CumulativePercent, pareto.Rows[2].CumulativePercent));

        var byCount = await client.GetFromJsonAsync<ParetoDto>($"/api/downtime/pareto?from={D(at)}&to={D(at)}&machine={code}&shift=S1&by=count");
        Assert.Equal(["(Down)", "FDR-JAM", "MAT-WAIT"], byCount!.Rows.Select(r => r.Reason));   // a tie: by code
        var withPlanned = await client.GetFromJsonAsync<ParetoDto>(
            $"/api/downtime/pareto?from={D(at)}&to={D(at)}&machine={code}&shift=S1&includePlanned=true");
        Assert.Contains(withPlanned!.Rows, r => r is { Reason: "MEETING", Minutes: 15.0, CountsAgainstAvailability: false });
    }

    // Step 9 / DoD: the two warning badges, on the closed snapshot and on the dashboard call. Numbers stay raw.
    [Fact]
    public async Task Missing_scans_and_a_wrong_cycle_time_are_flagged()
    {
        var client = await api.LoginOperatorAsync();

        // 1,000 counted, 940 scanned (910 OK + 30 NG): 6 % > 2 %, and Q = 91 % although only 3 % were NG (spec 1.2.10).
        var at = PastDay(21);
        var missing = await SampleShiftAsync(api, at, okScans: 910);
        await CloseAsync(missing, at(14, 5, 0));
        var code = await CodeAsync(missing);
        var snapshot = Assert.Single((await client.GetFromJsonAsync<List<OeeSnapshotDto>>(
            $"/api/oee/history?from={D(at)}&to={D(at)}&machine={code}"))!, h => h.Shift == "S1");
        Assert.Equal(["CounterMismatch"], snapshot.Figures.Warnings);
        Assert.Equal((1000L, 940L, 0.91), (snapshot.Figures.TotalCount, snapshot.Figures.ScanCount, snapshot.Figures.Quality!.Value));
        var shift = (await client.GetFromJsonAsync<ShiftOeeDto>($"/api/oee/shift?date={D(at)}&shift=S1&machine={code}"))!;
        Assert.Equal(["CounterMismatch"], shift.Total!.Warnings);

        // The WO says 30 s a board where the machine makes one every 21.6 s of run time: P = 30,000 / 21,600 = 139 %.
        at = PastDay(22);
        var slowCycle = await SampleShiftAsync(api, at, idealCycleSec: 30m);
        await CloseAsync(slowCycle, at(14, 5, 0));
        code = await CodeAsync(slowCycle);
        shift = (await client.GetFromJsonAsync<ShiftOeeDto>($"/api/oee/shift?date={D(at)}&shift=S1&machine={code}"))!;
        Assert.Equal(["PerformanceOver100"], shift.Total!.Warnings);
        Assert.Equal(30_000.0 / 21_600, shift.Total.Performance!.Value, 6);   // kept raw, not cut to 100 %
        Assert.Equal(360.0 / 405 * (30_000.0 / 21_600) * 0.97, shift.Total.Oee!.Value, 6);   // A x P x Q
    }

    // Step 8: the dashboard call. One shift of the machine, computed on the server, with its closed hours.
    [Fact]
    public async Task Shift_view_adds_up_on_the_server_with_its_hours()
    {
        var at = PastDay(18);   // Friday
        var plant = await SampleShiftAsync(api, at);
        var code = await CodeAsync(plant);
        await CloseAsync(plant, at(14, 5, 0));
        var client = await api.LoginOperatorAsync();

        var dto = (await client.GetFromJsonAsync<ShiftOeeDto>($"/api/oee/shift?date={D(at)}&shift=S1&machine={code}"))!;

        Assert.Equal(("S1", false), (dto.Shift, dto.IsRunning));
        Assert.Equal(0.7984, dto.Total!.Oee!.Value, 4);
        Assert.Equal([6, 7, 8, 9, 10, 11, 12, 13], dto.Hours.Select(h => h.Hour));
        Assert.Equal(970, dto.Hours.Sum(h => h.Figures.GoodCount));
        Assert.Equal((45 * 60, 30 * 60), (dto.Hours.Single(h => h.Hour == 10).Figures.PlannedSeconds,   // break 10:00-10:15
            dto.Hours.Single(h => h.Hour == 11).Figures.PlannedSeconds));                                // break from 11:30
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/api/oee/shift?date={D(at)}&machine={code}")).StatusCode);
    }

    [Fact]
    public async Task Excel_files_hold_numbers_with_formats_in_plant_time()
    {
        var at = PastDay(16);
        var plant = await SampleShiftAsync(api, at);
        var code = await CodeAsync(plant);
        await CloseAsync(plant, at(14, 5, 0));
        var client = await api.LoginAsync("sup01");

        var response = await client.GetAsync($"/api/reports/oee.xlsx?from={D(at)}&to={D(at)}&line=L1");
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", response.Content.Headers.ContentType!.MediaType);
        using (var wb = new XLWorkbook(await response.Content.ReadAsStreamAsync()))
        {
            var ws = wb.Worksheet("OEE");
            var row = ws.RowsUsed().Single(r => r.Cell(3).GetString() == code && r.Cell(2).GetString() == "S1");
            Assert.Equal(0.7984, row.Cell(14).GetDouble(), 4);                      // a number...
            Assert.Equal(OeeWorkbook.Percent, row.Cell(14).Style.NumberFormat.Format);   // ...shown as %
            Assert.Equal(new DateTime(2026, 9, 16, 6, 0, 0), row.Cell(4).GetDateTime()); // plant time, not 23:00 UTC
            Assert.Equal(1000, row.Cell(8).GetDouble());
            Assert.Contains("Asia/Ho_Chi_Minh", ws.Cell(1, 1).GetString());
            Assert.StartsWith("OEE = Availability", wb.Worksheet("Definitions").Cell(1, 1).GetString());
        }

        var pareto = await client.GetAsync($"/api/reports/downtime-pareto.xlsx?from={D(at)}&to={D(at)}&line=L1");
        pareto.EnsureSuccessStatusCode();
        using (var wb = new XLWorkbook(await pareto.Content.ReadAsStreamAsync()))
        {
            var first = wb.Worksheet("Pareto").Row(4);
            Assert.InRange(first.Cell(6).GetDouble(), 0.0, 1.0);
            Assert.Equal(OeeWorkbook.Percent, first.Cell(6).Style.NumberFormat.Format);
            Assert.True(wb.TryGetWorksheet("Definitions", out _));
        }
    }

    // Spec step 7 check: a Supervisor changes the reason, an Operator only reads; the audit names the supervisor.
    [Fact]
    public async Task Supervisor_classifies_a_stop_operator_cannot()
    {
        var at = PastDay(17);
        var plant = await SampleShiftAsync(api, at);
        var op = await api.LoginOperatorAsync();
        var sup = await api.LoginAsync("sup01");
        var from = at(0, 0, 0).ToString("O", CultureInfo.InvariantCulture);
        var to = at(23, 0, 0).ToString("O", CultureInfo.InvariantCulture);

        var list = await op.GetFromJsonAsync<List<UnclassifiedDowntimeDto>>($"/api/downtime/unclassified?machineId={plant.MachineId}&from={from}&to={to}");
        var qcWait = Assert.Single(list!);                                              // the 8-minute stop at 12:40
        Assert.Equal((8.0, "Down", "L1"), (qcWait.Minutes, qcWait.State, qcWait.Line));
        var reason = await api.WithDbAsync(db => db.DowntimeReasons.Where(r => r.Code == "QC-WAIT").Select(r => r.Id).SingleAsync());

        var forbidden = await op.PutAsJsonAsync($"/api/downtime/{qcWait.Id}/reason", new AssignDowntimeReasonRequest(reason, null));
        var ok = await sup.PutAsJsonAsync($"/api/downtime/{qcWait.Id}/reason", new AssignDowntimeReasonRequest(reason, null));

        Assert.Equal((HttpStatusCode.Forbidden, HttpStatusCode.OK), (forbidden.StatusCode, ok.StatusCode));
        Assert.Equal("QC-WAIT", (await ok.Content.ReadFromJsonAsync<DowntimeSegmentDto>())!.DowntimeReason);
        Assert.Equal("sup01", await api.WithDbAsync(db => db.AuditLogs
            .Where(a => a.TableName == "MachineStateHistory" && a.RecordKey == qcWait.Id.ToString() && a.ColumnName == "DowntimeReasonId")
            .Select(a => a.ChangedBy).SingleAsync()));
        var other = await api.WithDbAsync(db => db.DowntimeReasons.Where(r => r.Code == "OTHER").Select(r => r.Id).SingleAsync());
        Assert.Equal(HttpStatusCode.BadRequest,
            (await sup.PutAsJsonAsync($"/api/downtime/{qcWait.Id}/reason", new AssignDowntimeReasonRequest(other, null))).StatusCode);
    }

    [Fact]
    public async Task Bad_requests_are_refused()
    {
        var client = await api.LoginAsync("sup01");

        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/oee/current")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/oee/current?machine=L1-SMT-01&line=L1")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/oee/current?machine=NO-SUCH")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/oee/history?from=2026-09-01&to=2026-09-01&line=NO-SUCH")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/downtime/pareto?from=2026-08-01&to=2026-09-15")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/downtime/pareto?from=2026-09-01&to=2026-09-01&by=hours")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/reports/oee.xlsx?from=2026-09-15&to=2026-09-01")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.Anonymous().GetAsync("/api/oee/current?line=L1")).StatusCode);
    }

    // The running shift, on the real clock: cut at now, every machine of the line, the line total added up.
    [Fact]
    public async Task Current_shift_is_computed_up_to_now()
    {
        var plant = await NewMachineAsync(api);
        var code = await CodeAsync(plant);
        await api.WithDbAsync(async db =>
        {
            await StatesAsync(db, plant, (DateTime.UtcNow.AddHours(-3), MachineState.Running, null));
            return await db.SaveChangesAsync();
        });
        var client = await api.LoginOperatorAsync();

        var one = (await client.GetFromJsonAsync<CurrentOeeDto>($"/api/oee/current?machine={code}"))!;
        var line = (await client.GetFromJsonAsync<CurrentOeeDto>("/api/oee/current?line=L1"))!;

        if (one.Shift is null)   // run on a Sunday or a holiday: no shift, nothing to judge
        {
            Assert.Empty(one.Machines);
            return;
        }
        var mine = Assert.Single(one.Machines).Figures;
        Assert.InRange(one.ToUtc!.Value, DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(1));
        Assert.True(mine.PlannedSeconds <= (one.ToUtc.Value - one.FromUtc!.Value).TotalSeconds + 1);
        Assert.Equal(mine.PlannedSeconds, mine.RunSeconds);                     // running the whole time: A = 100 %
        Assert.Contains(line.Machines, m => m.Machine == code);
        // Seconds are truncated per machine and for the total: they may differ by under a second per machine.
        Assert.InRange(line.Total!.PlannedSeconds - line.Machines.Sum(m => (long)m.Figures.PlannedSeconds), 0, line.Machines.Count);
    }
}
