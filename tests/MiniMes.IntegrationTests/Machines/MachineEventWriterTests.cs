using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MiniMes.Application.Machines;
using MiniMes.Domain.Machines;
using MiniMes.Domain.MasterData.Equipment;
using MiniMes.Infrastructure.Machines;
using MiniMes.IntegrationTests.Infrastructure;

namespace MiniMes.IntegrationTests.Machines;

// Spec M4 step 4: the cold path against the real database. The hosted writer is off in Testing (no broker), so each
// test runs its own writer on its own channel, exactly as the host would.
[Collection(ApiCollection.Name)]
public class MachineEventWriterTests(ApiFixture api)
{
    private static readonly DateTime T0 = new(2026, 10, 17, 2, 0, 0, DateTimeKind.Utc);

    private IServiceScopeFactory Scopes => api.Factory.Services.GetRequiredService<IServiceScopeFactory>();

    [Fact]
    public async Task Events_and_state_segments_are_stored_in_order_without_overlap()
    {
        var m = await NewMachineAsync();
        var store = new MachineRuntimeStore();

        await WriteAsync(
            Apply(store, m, "state", T0, state: Contracts.Machines.MachineState.Running),
            Apply(store, m, "count", T0.AddSeconds(1), counter: 1000),
            Apply(store, m, "count", T0.AddSeconds(13), counter: 1001),
            Apply(store, m, "state", T0.AddMinutes(1), state: Contracts.Machines.MachineState.Down),
            Apply(store, m, "alarm", T0.AddMinutes(1), alarm: "E205"),
            Apply(store, m, "state", T0.AddMinutes(4), state: Contracts.Machines.MachineState.Running),
            Apply(store, m, "count", T0.AddMinutes(5), counter: 1006));

        var segments = await api.WithDbAsync(db => db.MachineStateHistory.Where(h => h.MachineId == m).OrderBy(h => h.StartAt).ToListAsync());
        Assert.Equal([MachineState.Running, MachineState.Down, MachineState.Running], segments.Select(s => s.State));
        Assert.Single(segments, s => s.EndAt == null);
        Assert.Equal(segments[1].StartAt, segments[0].EndAt);   // back to back: no gap, no overlap
        Assert.Equal(segments[2].StartAt, segments[1].EndAt);

        var events = await api.WithDbAsync(db => db.MachineEvents.Where(e => e.MachineId == m).ToListAsync());
        Assert.Equal(7, events.Count);
        Assert.Equal(6, events.Sum(e => e.Delta ?? 0));          // 1006 - 1000: counter last - counter first
    }

    // The RAM cache is empty after an API restart: a redelivered message reaches the DB, the unique index stops it,
    // and only that row is skipped, the rest of the batch is stored.
    [Fact]
    public async Task Duplicate_msg_id_already_stored_skips_only_that_row()
    {
        var m = await NewMachineAsync();
        var stored = MachineEvent.Alarm(Guid.NewGuid(), m, "E205", T0, T0);
        await api.WithDbAsync(db => { db.MachineEvents.Add(MachineEvent.Alarm(stored.MsgId, m, "E205", T0, T0)); return db.SaveChangesAsync(); });

        await WriteAsync(
            new IngestItem(MachineEvent.Alarm(Guid.NewGuid(), m, "E101", T0.AddSeconds(1), T0.AddSeconds(1)), null),
            new IngestItem(stored, null),
            new IngestItem(MachineEvent.Alarm(Guid.NewGuid(), m, null, T0.AddSeconds(2), T0.AddSeconds(2)), new StateChange(m, MachineState.Running, T0.AddSeconds(2))));

        var codes = await api.WithDbAsync(db => db.MachineEvents.Where(e => e.MachineId == m).OrderBy(e => e.MachineTs).Select(e => e.AlarmCode).ToListAsync());
        Assert.Equal(["E205", "E101", null], codes);
        Assert.Equal(1, await api.WithDbAsync(db => db.MachineStateHistory.CountAsync(h => h.MachineId == m)));
    }

    // A batch written again after a failure must not open a second segment.
    [Fact]
    public async Task Close_and_open_is_idempotent_and_ignores_older_changes()
    {
        var m = await NewMachineAsync();

        await WriteAsync(
            new IngestItem(null, new StateChange(m, MachineState.Down, T0)),
            new IngestItem(null, new StateChange(m, MachineState.Down, T0)),
            new IngestItem(null, new StateChange(m, MachineState.Running, T0.AddMinutes(-1))));

        var segment = await api.WithDbAsync(db => db.MachineStateHistory.SingleAsync(h => h.MachineId == m));
        Assert.Equal((MachineState.Down, T0, (DateTime?)null), (segment.State, segment.StartAt, segment.EndAt));
    }

    // Stopping the host writes what is still queued (a deploy must not lose the last second).
    [Fact]
    public async Task Stopping_drains_the_channel()
    {
        var m = await NewMachineAsync();
        var channel = MachineEventBatchWriter.CreateChannel();
        foreach (var i in Enumerable.Range(0, 250))
            channel.Writer.TryWrite(new IngestItem(MachineEvent.Alarm(Guid.NewGuid(), m, "E" + i, T0.AddSeconds(i), T0), null));

        await RunAndStopAsync(channel);     // started and stopped at once: the 250 queued rows are still written

        Assert.Equal(250, await api.WithDbAsync(db => db.MachineEvents.CountAsync(e => e.MachineId == m)));
    }

    // At ingest start the store gets the open segment and the last counter back: no lost delta, no split segment.
    [Fact]
    public async Task Loader_seeds_the_store_from_the_database()
    {
        var m = await NewMachineAsync();
        var before = new MachineRuntimeStore();
        await WriteAsync(
            Apply(before, m, "state", T0, state: Contracts.Machines.MachineState.Setup),
            Apply(before, m, "count", T0.AddSeconds(1), counter: 500),
            Apply(before, m, "count", T0.AddSeconds(20), counter: 520));

        var after = new MachineRuntimeStore();     // "restart"
        await MachineRuntimeLoader.LoadAsync(Scopes, after, default);

        Assert.Equal((MachineState.Setup, T0, 520L), (after.Get(m)!.State, after.Get(m)!.StateSince!.Value, after.Get(m)!.LastCounter!.Value));
        Assert.Equal(5L, Apply(after, m, "count", T0.AddSeconds(40), counter: 525)!.Event!.Delta);
        Assert.Null(Apply(after, m, "state", T0.AddSeconds(41), state: Contracts.Machines.MachineState.Setup));   // same state: no new segment
    }

    // ---------- helpers ----------

    private static IngestItem? Apply(MachineRuntimeStore store, int machineId, string kind, DateTime ts,
        Contracts.Machines.MachineState? state = null, long? counter = null, string? alarm = null)
    {
        var msg = new Contracts.Machines.MachineMessage(Guid.NewGuid(), "T", new DateTimeOffset(ts), state, counter, AlarmCode: alarm);
        return store.Apply(machineId, kind, msg, ts).Item;
    }

    private async Task WriteAsync(params IngestItem?[] items)
    {
        var channel = MachineEventBatchWriter.CreateChannel();
        foreach (var item in items.OfType<IngestItem>()) await channel.Writer.WriteAsync(item);
        await RunAndStopAsync(channel);
    }

    private async Task RunAndStopAsync(System.Threading.Channels.Channel<IngestItem> channel)
    {
        var writer = new MachineEventBatchWriter(channel, Scopes, TimeProvider.System, NullLogger<MachineEventBatchWriter>.Instance);
        await writer.StartAsync(default);
        await writer.StopAsync(default);
    }

    private Task<int> NewMachineAsync() => api.WithDbAsync(async db =>
    {
        var code = "T4-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var wc = await db.WorkCenters.Where(w => w.Code == "WC-SMT").Select(w => w.Id).SingleAsync();
        var machine = new Machine(wc, code, "Test machine " + code, "PickAndPlace", code, MachineDataSource.Mqtt);
        db.Machines.Add(machine);
        await db.SaveChangesAsync();
        return machine.Id;
    });
}
