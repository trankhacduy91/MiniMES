using MiniMes.Application.Machines;
using MiniMes.Contracts.Machines;
using MiniMes.Domain.Machines;
using DomainState = MiniMes.Domain.Machines.MachineState;
using WireState = MiniMes.Contracts.Machines.MachineState;

namespace MiniMes.UnitTests.Machines;

// Spec M4 step 4: the hot path turns a message into the rows to store, without the database.
public class MachineRuntimeStoreTests
{
    private const int M = 7;
    private static readonly DateTime T0 = new(2026, 10, 17, 1, 0, 0, DateTimeKind.Utc);

    private readonly MachineRuntimeStore _store = new();

    // ---------- count ----------

    [Fact]
    public void Counts_become_deltas_and_unchanged_counts_are_not_stored()
    {
        var first = Count(15230, T0);
        var next = Count(15235, T0.AddSeconds(12));
        var same = Count(15235, T0.AddSeconds(30));

        Assert.Equal((0L, false), (first.Item!.Event!.Delta!.Value, first.Item.Event.IsReset));   // baseline
        Assert.Equal(5L, next.Item!.Event!.Delta);
        Assert.Null(same.Item);                                                               // keep-alive count
        Assert.Equal(15235, _store.Get(M)!.LastCounter);
    }

    [Fact]
    public void Reset_is_flagged_and_counts_what_was_made_since()
    {
        Count(15290, T0);

        var reset = Count(3, T0.AddSeconds(12)).Item!.Event!;

        Assert.Equal((3L, true, MachineEventKind.Count), (reset.Delta!.Value, reset.IsReset, reset.Kind));
    }

    // A late message with a lower counter would otherwise read as a reset and count production twice.
    [Fact]
    public void Count_older_than_the_last_one_is_dropped_and_does_not_move_the_baseline()
    {
        Count(100, T0.AddSeconds(20));

        var late = Count(95, T0.AddSeconds(10));
        var next = Count(110, T0.AddSeconds(30));

        Assert.Null(late.Item);
        Assert.Contains("older", late.Note);
        Assert.Equal((10L, false), (next.Item!.Event!.Delta!.Value, next.Item.Event.IsReset));
    }

    [Fact]
    public void Count_with_quality_bad_is_not_used()
    {
        Count(100, T0);

        var bad = _store.Apply(M, MachineKinds.Count, Msg(T0.AddSeconds(5)) with { Counter = 0, Quality = MiniMes.Contracts.Machines.DataQuality.Bad }, T0.AddSeconds(5));
        var next = Count(104, T0.AddSeconds(10));

        Assert.Null(bad.Item);
        Assert.Equal(4L, next.Item!.Event!.Delta);
    }

    // After an API restart the store is seeded from the DB: the first count gives the production since, not 0.
    [Fact]
    public void Seeded_counter_gives_the_delta_after_a_restart()
    {
        _store.Seed(M, DomainState.Running, T0, lastCounter: 15230, lastCountAt: T0);

        Assert.Equal(60L, Count(15290, T0.AddMinutes(10)).Item!.Event!.Delta);
    }

    // ---------- state ----------

    [Fact]
    public void State_change_opens_a_segment_and_a_repeated_state_is_not_stored()
    {
        var first = State(WireState.Running, T0);
        var again = State(WireState.Running, T0.AddSeconds(5));       // retained state after a reconnect
        var down = State(WireState.Down, T0.AddMinutes(3));

        Assert.Equal(new StateChange(M, DomainState.Running, T0), first.Item!.StateChange);
        Assert.Null(again.Item);                                        // not stored, the segment stays as it is
        Assert.Equal(new StateChange(M, DomainState.Down, T0.AddMinutes(3)), down.Item!.StateChange);
        Assert.Equal((DomainState.Down, T0.AddMinutes(3)), (_store.Get(M)!.State, _store.Get(M)!.StateSince!.Value));
    }

    [Fact]
    public void Heartbeat_is_not_stored_unless_it_reveals_a_new_state()
    {
        State(WireState.Running, T0);

        var usual = Heartbeat(WireState.Running, T0.AddSeconds(5));
        var revealing = Heartbeat(WireState.Down, T0.AddSeconds(10));  // the state message was lost

        Assert.Null(usual.Item);
        Assert.Equal((MachineEventKind.State, DomainState.Down), (revealing.Item!.Event!.Kind, revealing.Item.StateChange!.State));
    }

    [Fact]
    public void State_older_than_the_open_segment_is_an_event_only()
    {
        State(WireState.Down, T0.AddMinutes(5));

        var late = State(WireState.Running, T0.AddMinutes(4));

        Assert.NotNull(late.Item!.Event);
        Assert.Null(late.Item.StateChange);
        Assert.Equal(DomainState.Down, _store.Get(M)!.State);
    }

    // ---------- time ----------

    [Theory]
    [InlineData(60, false)]     // within the tolerance: the machine's own time is kept
    [InlineData(-60, false)]
    [InlineData(61, true)]      // PLC clock off: the received time is used
    [InlineData(-3600, true)]
    public void Machine_time_is_used_only_within_60_seconds(int skewSec, bool skewed)
    {
        var received = T0.AddMinutes(10);

        var result = _store.Apply(M, MachineKinds.Alarm, Msg(received.AddSeconds(skewSec)) with { AlarmCode = "E205" }, received);

        Assert.Equal(skewed, result.ClockSkewed);
        Assert.Equal(skewed ? received : received.AddSeconds(skewSec), result.Item!.Event!.MachineTs);
        Assert.Equal(received, result.Item.Event.ReceivedAt);
        Assert.Equal(received, _store.Get(M)!.LastSeenAt);
    }

    [Fact]
    public void Process_kind_is_not_stored_by_the_event_path()
        => Assert.Null(_store.Apply(M, MachineKinds.Process, Msg(T0) with { Tag = "peakTemp", Value = 245 }, T0).Item);

    // ---------- helpers ----------

    private IngestResult Count(long counter, DateTime ts)
        => _store.Apply(M, MachineKinds.Count, Msg(ts) with { Counter = counter }, ts);

    private IngestResult State(WireState state, DateTime ts)
        => _store.Apply(M, MachineKinds.State, Msg(ts) with { State = state }, ts);

    private IngestResult Heartbeat(WireState state, DateTime ts)
        => _store.Apply(M, MachineKinds.Heartbeat, Msg(ts) with { State = state }, ts);

    private static MachineMessage Msg(DateTime ts) => new(Guid.NewGuid(), "L1-SMT-01", new DateTimeOffset(ts));
}
