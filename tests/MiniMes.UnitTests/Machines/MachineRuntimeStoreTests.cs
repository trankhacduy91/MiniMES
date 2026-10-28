using MiniMes.Application.Machines;
using MiniMes.Contracts.Machines;
using MiniMes.Domain.Machines;
using DataQuality = MiniMes.Domain.Machines.DataQuality;
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

    // [M4b] A gateway message that waited in the outbox while the broker was away keeps its ts (up to 7 days late);
    // a direct MQTT machine does not (its old ts is a wrong clock), and nobody sends from the future.
    [Theory]
    [InlineData("opcua", -87, false)]          // broker away 87 s: late, not skewed
    [InlineData("modbus", -6 * 24 * 3600, false)]
    [InlineData("modbus", -8 * 24 * 3600, true)] // older than the outbox can hold: a clock problem
    [InlineData("opcua", 61, true)]            // in the future: always the clock
    [InlineData("mqtt", -87, true)]            // direct machine: unchanged M4 rule
    public void Late_gateway_messages_keep_their_time(string source, int skewSec, bool skewed)
    {
        var received = T0.AddDays(10);

        var result = _store.Apply(M, MachineKinds.Alarm, Msg(received.AddSeconds(skewSec)) with { AlarmCode = "301", Source = source }, received);

        Assert.Equal(skewed, result.ClockSkewed);
        Assert.Equal(skewed ? received : received.AddSeconds(skewSec), result.Item!.Event!.MachineTs);
    }

    // The case met in M4b step 5: after a 87 s outage the outbox replays counts 87, 60 and 30 s old, then a fresh one.
    // Before the fix the first was moved to "now" and the next two were dropped as older: right total, wrong detail.
    [Fact]
    public void A_replayed_outbox_keeps_every_count_at_its_own_time()
    {
        var received = T0.AddMinutes(30);
        Count(100, T0.AddMinutes(28));   // last count before the outage
        long[] counters = [101, 102, 103, 104];
        int[] ago = [87, 60, 30, 0];

        var results = counters.Zip(ago, (c, s) => _store.Apply(M, MachineKinds.Count,
            Msg(received.AddSeconds(-s)) with { Counter = c, Source = "modbus" }, received)).ToList();

        Assert.All(results, r => Assert.Equal(1, r.Item!.Event!.Delta));
        Assert.Equal(ago.Select(s => received.AddSeconds(-s)), results.Select(r => r.Item!.Event!.MachineTs));
    }

    [Fact]
    public void Late_gateway_process_value_keeps_its_time()
    {
        var received = T0.AddMinutes(10);

        var result = _store.ApplyProcess(PeakTemp, Process(245.3, received.AddSeconds(-90)) with { Source = "opcua" }, received);

        Assert.False(result.ClockSkewed);
        Assert.Equal(received.AddSeconds(-90), result.Item!.ProcessValue!.SourceTs);
    }

    [Fact]
    public void Process_kind_is_not_stored_by_the_event_path()
        => Assert.Null(_store.Apply(M, MachineKinds.Process, Msg(T0) with { Tag = "peakTemp", Value = 245 }, T0).Item);

    // ---------- process tags [v2] ----------

    private static readonly MachineTag PeakTemp = new(M, "peakTemp", "°C", 0, 400);

    [Fact]
    public void Process_reading_is_stored_and_becomes_the_latest_value()
    {
        var result = _store.ApplyProcess(PeakTemp, Process(245.3, T0), T0);

        var value = result.Item!.ProcessValue!;
        Assert.Equal((245.3, DataQuality.Good, T0), (value.Value, value.Quality, value.SourceTs));
        Assert.Null(result.Item.Event);
        Assert.Equal(new ProcessReading("peakTemp", "°C", 245.3, DataQuality.Good, T0), Assert.Single(_store.Get(M)!.Process));
    }

    // A cut sensor wire reads -32768: kept for diagnosis, but Bad so it never reaches SPC (spec 2.3b).
    [Fact]
    public void Value_outside_the_plausible_range_is_stored_as_bad_with_a_note()
    {
        var result = _store.ApplyProcess(PeakTemp, Process(-32768, T0), T0);

        Assert.Equal(DataQuality.Bad, result.Item!.ProcessValue!.Quality);
        Assert.Contains("plausible", result.Note);
    }

    [Fact]
    public void Value_reported_bad_stays_bad_without_a_warning()
    {
        var result = _store.ApplyProcess(PeakTemp, Process(245, T0) with { Quality = MiniMes.Contracts.Machines.DataQuality.Bad }, T0);

        Assert.Equal((DataQuality.Bad, (string?)null), (result.Item!.ProcessValue!.Quality, result.Note));
    }

    [Fact]
    public void Late_reading_is_stored_but_does_not_replace_the_latest_value()
    {
        _store.ApplyProcess(PeakTemp, Process(246, T0.AddSeconds(10)), T0.AddSeconds(10));

        var late = _store.ApplyProcess(PeakTemp, Process(240, T0), T0.AddSeconds(11));

        Assert.NotNull(late.Item!.ProcessValue);
        Assert.Equal(246, Assert.Single(_store.Get(M)!.Process).Value);
    }

    // ---------- throttle (step 6) ----------

    // 10 counts between two pushes = one push with the latest total: nothing lost, the value is absolute.
    [Fact]
    public void Take_dirty_gives_the_latest_counter_once_per_change()
    {
        foreach (var i in Enumerable.Range(0, 10)) Count(15000 + i, T0.AddMilliseconds(200 * i));

        var first = _store.TakeDirty(T0.AddSeconds(10));
        var second = _store.TakeDirty(T0.AddSeconds(10));

        Assert.Equal((M, 15009L), (first.Counters.Single().MachineId, first.Counters.Single().Counter));
        Assert.Empty(second.Counters);                                  // flags cleared
        Count(15009, T0.AddSeconds(10));                                // unchanged keep-alive count
        Assert.Empty(_store.TakeDirty(T0.AddSeconds(10)).Counters);
    }

    [Fact]
    public void Take_dirty_gives_each_changed_tag_with_its_latest_reading()
    {
        _store.ApplyProcess(PeakTemp, Process(245.0, T0), T0);
        _store.ApplyProcess(PeakTemp, Process(246.5, T0.AddSeconds(1)), T0.AddSeconds(1));

        var reading = Assert.Single(_store.TakeDirty(T0.AddSeconds(10)).Process).Reading;

        Assert.Equal(246.5, reading.Value);
        Assert.Empty(_store.TakeDirty(T0.AddSeconds(10)).Process);
    }

    // State changes are not part of the throttled set: they are pushed at once by the caller.
    [Fact]
    public void State_change_does_not_mark_anything_dirty()
    {
        State(WireState.Down, T0);

        var dirty = _store.TakeDirty(T0.AddSeconds(10));

        Assert.Empty(dirty.Counters);
        Assert.Empty(dirty.Process);
    }

    // ---------- count of the current hour (step 8) ----------

    // Sum of the deltas, not "counter now - counter at :00": a reset in the middle of the hour does not break it.
    [Fact]
    public void Count_this_hour_sums_the_deltas_across_a_reset()
    {
        Count(15000, T0.AddMinutes(1));                 // baseline: 0
        Count(15010, T0.AddMinutes(10));                // +10
        Count(4, T0.AddMinutes(20));                    // reset: +4

        Assert.Equal(14, _store.CountThisHour(_store.Get(M)!, T0.AddMinutes(30)));
    }

    [Fact]
    public void A_new_hour_starts_from_the_first_delta_in_it()
    {
        Count(100, T0.AddMinutes(10));
        Count(105, T0.AddMinutes(59.9));                // 01:59:54 -> hour 01:00
        Count(108, T0.AddMinutes(60.2));                // 02:00:12 -> hour 02:00

        Assert.Equal(3, _store.CountThisHour(_store.Get(M)!, T0.AddMinutes(61)));
        Assert.Equal(3, _store.TakeDirty(T0.AddMinutes(61)).Counters.Single().CountThisHour);
    }

    // A stopped machine made 0 this hour: the snapshot says so, and the publisher pushes the 0 once at the new hour.
    [Fact]
    public void At_a_new_hour_a_silent_machine_drops_to_zero_once()
    {
        Count(100, T0.AddMinutes(10));
        Count(105, T0.AddMinutes(20));
        _store.TakeDirty(T0.AddMinutes(30));

        Assert.Equal(0, _store.CountThisHour(_store.Get(M)!, T0.AddMinutes(61)));
        var atNewHour = _store.TakeDirty(T0.AddMinutes(60).AddSeconds(1));
        Assert.Equal((105L, 0L), (atNewHour.Counters.Single().Counter, atNewHour.Counters.Single().CountThisHour));
        Assert.Empty(_store.TakeDirty(T0.AddMinutes(60).AddSeconds(2)).Counters);
    }

    // After an API restart the loader seeds what the DB has for the hour; new counts add to it.
    [Fact]
    public void Seeded_hour_count_continues_after_a_restart()
    {
        _store.Seed(M, DomainState.Running, T0, lastCounter: 500, lastCountAt: T0.AddMinutes(5), hourStart: T0, hourCount: 40);

        Count(507, T0.AddMinutes(15));

        Assert.Equal(47, _store.CountThisHour(_store.Get(M)!, T0.AddMinutes(16)));
    }

    // ---------- helpers ----------

    private static MachineMessage Process(double value, DateTime ts)
        => Msg(ts) with { Tag = "peakTemp", Value = value };


    private IngestResult Count(long counter, DateTime ts)
        => _store.Apply(M, MachineKinds.Count, Msg(ts) with { Counter = counter }, ts);

    private IngestResult State(WireState state, DateTime ts)
        => _store.Apply(M, MachineKinds.State, Msg(ts) with { State = state }, ts);

    private IngestResult Heartbeat(WireState state, DateTime ts)
        => _store.Apply(M, MachineKinds.Heartbeat, Msg(ts) with { State = state }, ts);

    private static MachineMessage Msg(DateTime ts) => new(Guid.NewGuid(), "L1-SMT-01", new DateTimeOffset(ts));
}
