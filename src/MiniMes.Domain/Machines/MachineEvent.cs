using MiniMes.Domain.Common;

namespace MiniMes.Domain.Machines;

// Same names and order as Contracts.Machines.MachineState (the wire enum); Domain references nothing, so the
// enum exists twice and a unit test keeps them identical. Stored as tinyint (big tables).
public enum MachineState : byte { Running, Idle, Down, Setup, PlannedStop, Offline }

public enum DataQuality : byte { Good, Bad, Uncertain }

public enum MachineEventKind : byte
{
    State = 1,
    Count = 2,
    Alarm = 3,
    Connection = 4,   // online / offline inferred by the server (LWT, watchdog)
}

// What a machine said, one row per message that changed something (spec M4 2.5). Heartbeats and counts that did
// not move are not stored. Insert-only, not audited; MsgId unique = a QoS 1 message delivered twice is stored once.
public class MachineEvent
{
    public const int AlarmCodeMaxLength = 32;

    public long Id { get; private set; }
    public Guid MsgId { get; private set; }
    public int MachineId { get; private set; }
    public MachineEventKind Kind { get; private set; }
    public DateTime MachineTs { get; private set; }     // the machine's clock, UTC
    public DateTime ReceivedAt { get; private set; }    // the server's clock, UTC
    public MachineState? State { get; private set; }
    public long? Counter { get; private set; }
    public long? Delta { get; private set; }            // computed by the ingest (CounterMath)
    public bool IsReset { get; private set; }
    public int? GoodDelta { get; private set; }         // the machine's own split: cross-checking only, never OEE
    public int? NgDelta { get; private set; }
    public string? AlarmCode { get; private set; }      // null on an Alarm row = the alarm was cleared

    private MachineEvent() { }

    private MachineEvent(Guid msgId, int machineId, MachineEventKind kind, DateTime machineTs, DateTime receivedAt)
    {
        if (msgId == Guid.Empty) throw new DomainException("A machine event needs its message id.");
        MsgId = msgId;
        MachineId = machineId;
        Kind = kind;
        MachineTs = machineTs;
        ReceivedAt = receivedAt;
    }

    public static MachineEvent StateChanged(Guid msgId, int machineId, MachineState state, DateTime machineTs, DateTime receivedAt)
        => new(msgId, machineId, MachineEventKind.State, machineTs, receivedAt) { State = state };

    public static MachineEvent Count(Guid msgId, int machineId, long counter, CounterDelta delta, int? goodDelta, int? ngDelta,
        DateTime machineTs, DateTime receivedAt)
    {
        if (counter < 0) throw new DomainException("A cumulative counter cannot be negative.");
        return new(msgId, machineId, MachineEventKind.Count, machineTs, receivedAt)
        {
            Counter = counter, Delta = delta.Delta, IsReset = delta.IsReset, GoodDelta = goodDelta, NgDelta = ngDelta,
        };
    }

    public static MachineEvent Alarm(Guid msgId, int machineId, string? alarmCode, DateTime machineTs, DateTime receivedAt)
    {
        if (alarmCode is { Length: > AlarmCodeMaxLength })
            throw new DomainException($"Alarm code must be at most {AlarmCodeMaxLength} characters.");
        return new(msgId, machineId, MachineEventKind.Alarm, machineTs, receivedAt) { AlarmCode = alarmCode };
    }

    // online -> the state the machine reports next; offline -> Offline.
    public static MachineEvent Connection(Guid msgId, int machineId, bool online, DateTime at)
        => new(msgId, machineId, MachineEventKind.Connection, at, at) { State = online ? null : MachineState.Offline };
}
