using System.Collections.Concurrent;
using MiniMes.Contracts.Machines;
using MiniMes.Domain.Machines;
using DomainQuality = MiniMes.Domain.Machines.DataQuality;
using DomainState = MiniMes.Domain.Machines.MachineState;

namespace MiniMes.Application.Machines;

// What the batch writer has to store for one message (spec M4 2.4): the event row (null = nothing to store, e.g. a
// heartbeat or an unchanged count), the state segment to close/open (null = the state did not change) and, for a
// process tag [v2], the reading.
public sealed record IngestItem(MachineEvent? Event, StateChange? StateChange, ProcessValue? ProcessValue = null);

public sealed record StateChange(int MachineId, DomainState State, DateTime At);

// The latest known state of one machine, also read by the dashboard (step 6) and the watchdog (step 5).
public sealed record MachineRuntimeSnapshot(int MachineId, DomainState? State, DateTime? StateSince, long? LastCounter, DateTime? LastSeenAt,
    IReadOnlyList<ProcessReading> Process);

// [v2] The latest reading of one tag, for the dashboard (Bad is shown as such, not hidden).
public sealed record ProcessReading(string Tag, string Unit, double Value, DomainQuality Quality, DateTime SourceTs);

// The hot path (spec M4 2.4): one machine's latest state in RAM, so a message is turned into rows without reading
// the database. Rules:
//  - Delta comes from the cumulative counter (CounterMath). A count older than the last one processed is dropped:
//    taken as is, a late 15290 after 3 would read as a reset and add production twice.
//  - Time: the machine's ts when it is within 60 s of the server's, otherwise the received time (PLC clock off).
//  - A state change older than the start of the current segment is stored as an event only, history is not rewritten.
//  - Not stored: heartbeats, counts that did not change, and a state equal to the current one (the retained state
//    is delivered again on every reconnect; storing it would only add rows, and after an API restart it would hit
//    the unique MsgId index for nothing).
// Loaded from the database at startup (Seed), otherwise the first count after a restart would be a baseline and its
// production since the last stored count would be lost.
public sealed class MachineRuntimeStore
{
    public static readonly TimeSpan MaxClockSkew = TimeSpan.FromSeconds(60);

    private readonly ConcurrentDictionary<int, Runtime> _machines = new();

    public void Seed(int machineId, DomainState? state, DateTime? stateSince, long? lastCounter, DateTime? lastCountAt)
        => _machines[machineId] = new Runtime
        {
            State = state, StateSince = stateSince, LastCounter = lastCounter, LastCountAt = lastCountAt,
        };

    public MachineRuntimeSnapshot? Get(int machineId)
        => _machines.TryGetValue(machineId, out var r) ? r.Snapshot(machineId) : null;

    public IReadOnlyList<MachineRuntimeSnapshot> All() => _machines.Select(kv => kv.Value.Snapshot(kv.Key)).ToList();

    public IngestResult Apply(int machineId, string kind, MachineMessage msg, DateTime receivedAt)
    {
        var r = _machines.GetOrAdd(machineId, _ => new Runtime());
        var machineTs = msg.Ts.UtcDateTime;
        var skewed = (machineTs - receivedAt).Duration() > MaxClockSkew;
        var at = skewed ? receivedAt : machineTs;

        lock (r)
        {
            r.LastSeenAt = receivedAt;
            return kind switch
            {
                MachineKinds.State or MachineKinds.Heartbeat => ApplyState(r, machineId, msg, at, receivedAt, skewed),
                MachineKinds.Count => ApplyCount(r, machineId, msg, at, receivedAt, skewed),
                MachineKinds.Alarm => new(new IngestItem(MachineEvent.Alarm(msg.MsgId, machineId, msg.AlarmCode, at, receivedAt), null), skewed),
                _ => new(null, skewed, $"kind '{kind}' is not stored by the event path"),
            };
        }
    }

    // A heartbeat that reveals a new state (the state message was lost) is handled like a state message.
    private static IngestResult ApplyState(Runtime r, int machineId, MachineMessage msg, DateTime at, DateTime receivedAt, bool skewed)
    {
        var state = ToDomain(msg.State!.Value);
        if (state == r.State) return new(null, skewed);                 // unchanged: nothing to store

        var evt = MachineEvent.StateChanged(msg.MsgId, machineId, state, at, receivedAt);
        if (r.StateSince is { } since && at < since)
            return new(new IngestItem(evt, null), skewed, "state older than the current segment: event only");

        (r.State, r.StateSince) = (state, at);
        return new(new IngestItem(evt, new StateChange(machineId, state, at)), skewed);
    }

    private static IngestResult ApplyCount(Runtime r, int machineId, MachineMessage msg, DateTime at, DateTime receivedAt, bool skewed)
    {
        // [v2] A Bad reading (gateway could not read the PLC) never moves the counter baseline.
        if (msg.Quality == Contracts.Machines.DataQuality.Bad) return new(null, skewed, "count with quality Bad");
        if (r.LastCountAt is { } last && at < last) return new(null, skewed, "count older than the last one processed");

        var counter = msg.Counter!.Value;
        var delta = CounterMath.Compute(r.LastCounter, counter);
        (r.LastCounter, r.LastCountAt) = (counter, at);
        if (delta is { Delta: 0, IsBaseline: false, IsReset: false }) return new(null, skewed);   // unchanged: keep-alive count

        return new(new IngestItem(MachineEvent.Count(msg.MsgId, machineId, counter, delta, msg.GoodDelta, msg.NgDelta, at, receivedAt), null), skewed);
    }

    // [v2] Spec 2.3b: every reading of a known tag is stored (the gateway already sends only on change, deadband),
    // with the quality judged by the tag's physical range: a reported Good outside it becomes Bad.
    // Readings arriving out of order are stored with their own time; only a newer one replaces the latest.
    public IngestResult ApplyProcess(MachineTag tag, MachineMessage msg, DateTime receivedAt)
    {
        var r = _machines.GetOrAdd(tag.MachineId, _ => new Runtime());
        var sourceTs = msg.Ts.UtcDateTime;
        var skewed = (sourceTs - receivedAt).Duration() > MaxClockSkew;
        var at = skewed ? receivedAt : sourceTs;
        var reported = Enum.Parse<DomainQuality>(msg.Quality.ToString());
        var quality = tag.Judge(msg.Value!.Value, reported);

        lock (r)
        {
            r.LastSeenAt = receivedAt;
            if (!r.Process.TryGetValue(tag.TagName, out var latest) || at >= latest.SourceTs)
                r.Process[tag.TagName] = new ProcessReading(tag.TagName, tag.Unit, msg.Value.Value, quality, at);
        }
        var note = quality == DomainQuality.Bad && reported != DomainQuality.Bad
            ? $"value {msg.Value} outside the plausible range {tag.LowerPlausible}..{tag.UpperPlausible}: stored as Bad"
            : null;
        return new(new IngestItem(null, null, new ProcessValue(tag.Id, msg.Value.Value, quality, at, receivedAt, msg.MsgId)), skewed, note);
    }

    // The two enums are kept identical by a unit test (step 1): map by name.
    private static DomainState ToDomain(Contracts.Machines.MachineState state) => Enum.Parse<DomainState>(state.ToString());

    private sealed class Runtime
    {
        public DomainState? State;
        public DateTime? StateSince;
        public long? LastCounter;
        public DateTime? LastCountAt;
        public DateTime? LastSeenAt;
        public readonly Dictionary<string, ProcessReading> Process = [];

        public MachineRuntimeSnapshot Snapshot(int machineId)
        {
            lock (this) return new(machineId, State, StateSince, LastCounter, LastSeenAt, Process.Values.OrderBy(p => p.Tag).ToList());
        }
    }
}

// Item = what to store (null = nothing); Note = why something was not stored (logged at Debug by the ingest).
public sealed record IngestResult(IngestItem? Item, bool ClockSkewed, string? Note = null);
