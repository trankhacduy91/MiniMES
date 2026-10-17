using MiniMes.Contracts.Machines;

namespace MiniMes.MachineSimulator.Machines;

// What the console asks a machine to do. Queued and applied by the machine's own loop at the start of a tick,
// so the state is only ever changed by one thread (spec 2.8).
public abstract record MachineCommand;

public sealed record GoDown(int? Seconds) : MachineCommand;            // null = until "up"
public sealed record ChangeState(MachineState State) : MachineCommand; // up / setup / stop
public sealed record ForceNg(int Count) : MachineCommand;
public sealed record ResetCounter : MachineCommand;
public sealed record Mute(bool On) : MachineCommand;
public sealed record DuplicateNext : MachineCommand;
public sealed record SetSkew(int Seconds) : MachineCommand;

public sealed record MachineSnapshot(
    string Code, MachineState State, long Counter, long Good, long Ng,
    bool Connected, bool Muted, int SkewSec, DateTimeOffset? DownUntil);
