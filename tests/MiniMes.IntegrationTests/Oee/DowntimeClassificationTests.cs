using System.Net.Http.Json;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using MiniMes.Application.Common;
using MiniMes.Application.Oee;
using MiniMes.Application.Oee.Downtime;
using MiniMes.Contracts.Andon;
using MiniMes.Domain.Common;
using MiniMes.Domain.Execution;
using MiniMes.Domain.Machines;
using MiniMes.Domain.Oee;
using MiniMes.Infrastructure.Oee;
using MiniMes.IntegrationTests.Infrastructure;
using static MiniMes.IntegrationTests.Oee.OeeTestData;

namespace MiniMes.IntegrationTests.Oee;

// Spec M5 step 6: the "to classify" list, assigning a reason (rule 8: the shift's snapshots are recomputed), the
// audit of who changed it, and an Andon resolved with a reason classifying the stop it was raised for.
[Collection(ApiCollection.Name)]
public class DowntimeClassificationTests(ApiFixture api)
{
    private static readonly PlantTimeZone Plant = new(TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh"));

    private async Task<T> SendAsync<T>(IRequest<T> request)
    {
        using var scope = api.Factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ISender>().Send(request);
    }

    private Task<int> ReasonIdAsync(string code)
        => api.WithDbAsync(db => db.DowntimeReasons.Where(r => r.Code == code).Select(r => r.Id).SingleAsync());

    private Task<int> CloseAsync(OeeTestData.Plant plant, DateTime now) => api.WithDbAsync(db =>
    {
        var time = new FakeTimeProvider(new DateTimeOffset(now));
        var options = Options.Create(new OeeOptions());
        return new OeeSnapshotService(db, new OeeInputReader(db, time, options), Plant, time, options,
            NullLogger<OeeSnapshotService>.Instance).CloseDueBucketsAsync(CancellationToken.None, [plant.MachineId]);
    });

    // Other tests add shifts of their own to the shared database: only the seeded S1 is looked at.
    private Task<OeeSnapshot> ShiftTotalAsync(OeeTestData.Plant plant, DateTime inShift) => api.WithDbAsync(async db =>
    {
        var s1 = await db.Shifts.Where(s => s.Code == "S1").Select(s => s.Id).SingleAsync();
        var date = Plant.DateOf(inShift);
        return await db.OeeSnapshots.AsNoTracking()
            .SingleAsync(s => s.MachineId == plant.MachineId && s.ShiftId == s1 && s.ShiftDate == date && s.Hour == null);
    });

    // S1 of the day: running all shift except a 30-minute Down 07:00-07:30 nobody explained and a 30 s jam
    // (micro stop) at 09:00.
    private async Task<(OeeTestData.Plant Plant, long DownId, long JamId)> ShiftWithOneStopAsync(Func<int, int, int, DateTime> at)
    {
        var plant = await NewMachineAsync(api);
        var boards = await WorkOrderAsync(api, plant, 20m, 50, at(5, 0, 0));
        await api.WithDbAsync(async db =>
        {
            Counters(db, plant, (at(5, 55, 0), 0), (at(9, 0, 0), 600), (at(13, 0, 0), 1100));
            await StatesAsync(db, plant, [
                (at(6, 0, 0), MachineState.Running, null), (at(7, 0, 0), MachineState.Down, null),
                (at(7, 30, 0), MachineState.Running, null), (at(9, 0, 0), MachineState.Down, null),
                (at(9, 0, 30), MachineState.Running, null), (at(14, 0, 0), MachineState.Idle, null)]);
            Scans(db, plant, boards, ScanResult.Ok, at(8, 0, 0), TimeSpan.FromMinutes(1));
            return await db.SaveChangesAsync();
        });
        var ids = await api.WithDbAsync(db => db.MachineStateHistory.Where(h => h.MachineId == plant.MachineId && h.State == MachineState.Down)
            .OrderBy(h => h.StartAt).Select(h => h.Id).ToListAsync());
        return (plant, ids[0], ids[1]);
    }

    [Fact]
    public async Task Planned_maintenance_reason_recomputes_the_shift_and_raises_availability()
    {
        var at = Day(14);
        var (plant, downId, jamId) = await ShiftWithOneStopAsync(at);
        await CloseAsync(plant, at(14, 5, 0));
        var before = await ShiftTotalAsync(plant, at(8, 0, 0));
        Assert.Equal(390.0 / 420.0, before.Availability!.Value, 9);           // 30 min lost of 420 planned

        var list = await SendAsync(new GetUnclassifiedDowntimeQuery(plant.MachineId, at(0, 0, 0), at(23, 0, 0)));
        Assert.Equal([downId], list.Select(d => d.Id));                         // the 30 s jam is a micro stop
        Assert.Equal((30.0, "Down"), (list[0].Minutes, list[0].State));

        await SendAsync(new AssignDowntimeReasonCommand(downId, await ReasonIdAsync("PM"), "monthly PM"));

        var date = Plant.DateOf(at(8, 0, 0));
        var marked = await api.WithDbAsync(async db =>
        {
            var s1 = await db.Shifts.Where(s => s.Code == "S1").Select(s => s.Id).SingleAsync();
            return await db.OeeSnapshots.Where(s => s.MachineId == plant.MachineId && s.ShiftId == s1 && s.ShiftDate == date && !s.IsFinal)
                .Select(s => s.Hour).ToListAsync();
        });
        Assert.Equal([null, 7], marked.OrderBy(h => h ?? -1).Select(h => (int?)h));   // only the shift total and 07:00
        Assert.Empty(await SendAsync(new GetUnclassifiedDowntimeQuery(plant.MachineId, at(0, 0, 0), at(23, 0, 0))));

        await CloseAsync(plant, at(14, 30, 0));                                   // the job recomputes them
        var after = await ShiftTotalAsync(plant, at(8, 0, 0));
        Assert.Equal((390 * 60, 1.0), (after.PlannedSeconds, after.Availability));   // PM: not planned, no loss
        Assert.True(after.IsFinal);

        var audit = await api.WithDbAsync(db => db.AuditLogs.Where(a => a.TableName == "MachineStateHistory" && a.RecordKey == downId.ToString())
            .OrderBy(a => a.ColumnName).Select(a => new { a.ColumnName, a.OldValue, a.NewValue }).ToListAsync());
        Assert.Equal([("DowntimeNote", null, "monthly PM"), ("DowntimeReasonId", null, (await ReasonIdAsync("PM")).ToString())],
            audit.Select(a => (a.ColumnName, a.OldValue, a.NewValue)));
        // Machine-written changes (inserting and closing segments) stay out of the audit: only the reason columns.
        Assert.Equal(0, await api.WithDbAsync(db => db.AuditLogs.CountAsync(a => a.TableName == "MachineStateHistory" && a.RecordKey == jamId.ToString())));
    }

    [Fact]
    public async Task Reason_that_counts_against_availability_leaves_it_unchanged()
    {
        var at = Day(15);
        var (plant, downId, _) = await ShiftWithOneStopAsync(at);
        await CloseAsync(plant, at(14, 5, 0));
        var before = await ShiftTotalAsync(plant, at(8, 0, 0));

        await SendAsync(new AssignDowntimeReasonCommand(downId, await ReasonIdAsync("FDR-JAM"), null));
        await CloseAsync(plant, at(14, 30, 0));
        var after = await ShiftTotalAsync(plant, at(8, 0, 0));

        Assert.Equal(before.Availability, after.Availability);
        Assert.True(after.CalculatedAt > before.CalculatedAt);                   // recomputed all the same
    }

    [Fact]
    public async Task Invalid_assignments_are_refused()
    {
        var at = Day(16);
        var (plant, downId, _) = await ShiftWithOneStopAsync(at);
        var running = await api.WithDbAsync(db => db.MachineStateHistory
            .Where(h => h.MachineId == plant.MachineId && h.State == MachineState.Running).Select(h => h.Id).FirstAsync());
        var open = await api.WithDbAsync(db => db.MachineStateHistory
            .Where(h => h.MachineId == plant.MachineId && h.EndAt == null).Select(h => h.Id).SingleAsync());
        var other = await ReasonIdAsync("OTHER");

        await Assert.ThrowsAsync<ValidationException>(() => SendAsync(new AssignDowntimeReasonCommand(downId, other, "  ")));
        await Assert.ThrowsAsync<ValidationException>(() => SendAsync(new AssignDowntimeReasonCommand(downId, 999_999, null)));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => SendAsync(new AssignDowntimeReasonCommand(999_999_999, other, "x")));
        await Assert.ThrowsAsync<DomainException>(() => SendAsync(new AssignDowntimeReasonCommand(open, other, "x")));
        await Assert.ThrowsAsync<DomainException>(() => SendAsync(new AssignDowntimeReasonCommand(running, other, "x")));

        var ok = await SendAsync(new AssignDowntimeReasonCommand(downId, other, " operator unplugged the feeder "));
        Assert.Equal(("OTHER", "operator unplugged the feeder"), (ok.DowntimeReason, ok.Note));
    }

    // Spec 1.3 step 4: the supervisor resolves the Andon with FDR-JAM; the Down it was raised for gets that reason.
    [Fact]
    public async Task Resolving_an_andon_classifies_the_stop_it_was_raised_for()
    {
        var plant = await NewMachineAsync(api);
        var start = DateTime.UtcNow.AddMinutes(-10);
        var classifiedSetupId = await api.WithDbAsync(async db =>
        {
            var setup = new MachineStateHistory(plant.MachineId, MachineState.Setup, start.AddMinutes(-5));
            setup.AssignReason(await db.DowntimeReasons.Where(r => r.Code == "CHG-MODEL").Select(r => r.Id).SingleAsync());
            setup.Close(start);
            db.MachineStateHistory.Add(setup);
            db.MachineStateHistory.Add(new MachineStateHistory(plant.MachineId, MachineState.Down, start));
            await db.SaveChangesAsync();
            return setup.Id;
        });
        var op = await api.LoginOperatorAsync();                       // the operator calls, the supervisor answers
        var sup = await api.LoginAsync("sup01");
        var raised = (await (await op.PostAsJsonAsync("/api/andon", new RaiseAndonRequest(plant.MachineId, AndonTypes.Machine)))
            .Content.ReadFromJsonAsync<RaiseAndonResult>())!.Andon;
        await api.WithDbAsync(async db =>                                   // the machine runs again, then the call is resolved
        {
            var down = await db.MachineStateHistory.SingleAsync(h => h.MachineId == plant.MachineId && h.EndAt == null);
            down.Close(DateTime.UtcNow);
            db.MachineStateHistory.Add(new MachineStateHistory(plant.MachineId, MachineState.Running, DateTime.UtcNow));
            return await db.SaveChangesAsync();
        });

        var resolve = await sup.PostAsJsonAsync($"/api/andon/{raised.Id}/resolve",
            new ResolveAndonRequest(await ReasonIdAsync("FDR-JAM"), "reel bent", raised.RowVersion));
        resolve.EnsureSuccessStatusCode();

        var segments = await api.WithDbAsync(db => db.MachineStateHistory.Where(h => h.MachineId == plant.MachineId)
            .OrderBy(h => h.StartAt).Select(h => new { h.Id, h.State, h.DowntimeReasonId, h.DowntimeNote }).ToListAsync());
        Assert.Equal((MachineState.Down, await ReasonIdAsync("FDR-JAM"), "reel bent"),
            (segments[1].State, segments[1].DowntimeReasonId, segments[1].DowntimeNote));
        Assert.Equal(await ReasonIdAsync("CHG-MODEL"), segments.Single(s => s.Id == classifiedSetupId).DowntimeReasonId);   // ended before the call
        Assert.Null(segments[2].DowntimeReasonId);                                                                     // Running
    }
}
