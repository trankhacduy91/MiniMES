using MiniMes.Contracts.Machines;

namespace MiniMes.Contracts.Realtime;

// Spec M4 2.6: what the server pushes over SignalR. Typed hub: a method name typed wrong breaks the compile instead
// of silently never firing on the client. Each method name is the event name the client subscribes to.
// AndonChanged comes with the Andon backend (step 7).
public interface IMachineClient
{
    Task MachineStateChanged(MachineStateChangedDto dto);    // pushed at once
    Task MachineCounterUpdated(MachineCounterDto dto);       // throttled: at most once a second per machine
    Task MachineProcessUpdated(MachineProcessDto dto);       // [v2] throttled like the counter
}

public static class RealtimeHub
{
    public const string Path = "/hubs/machines";
    public const string JoinLine = nameof(JoinLine);
    public const string LeaveLine = nameof(LeaveLine);

    public static string LineGroup(string lineCode) => $"line:{lineCode}";
}

// Absolute values only (state, cumulative counter, latest reading), never "+3": a throttled or missed push is
// repaired by the next one, and a client can always replace what it shows (spec 2.7). Times are UTC.
public sealed record MachineStateChangedDto(string Machine, string Line, MachineState State, DateTime Since);

public sealed record MachineCounterDto(string Machine, string Line, long Counter, DateTime? LastSeenAt);

public sealed record MachineProcessDto(string Machine, string Line, string Tag, string Unit, double Value, DataQuality Quality, DateTime SourceTs);

// REST snapshot (GET /api/lines/{code}/status): loaded first and again after every reconnect, then kept up to date
// by the pushes above. SignalR does not replay what a disconnected client missed.
public sealed record LineStatusDto(string Line, IReadOnlyList<MachineStatusDto> Machines);

public sealed record MachineStatusDto(
    int Id, string Code, string Name, string Type, string DataSource,
    MachineState? State, DateTime? StateSince, long? Counter, DateTime? LastSeenAt,
    IReadOnlyList<ProcessReadingDto> Process);

public sealed record ProcessReadingDto(string Tag, string Unit, double Value, DataQuality Quality, DateTime SourceTs);
