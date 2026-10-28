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
public sealed record PublishTag(string Tag, double Value) : MachineCommand;   // [v2] until the M4b gateway sends them
public sealed record SetDrift(double? PerMin) : MachineCommand;               // M4b: oven temperature drift, null = off
public sealed record SetSensor(bool Ok) : MachineCommand;                     // M4b: thermocouple broken / fixed

public sealed record MachineSnapshot(
    string Code, MachineState State, long Counter, long Good, long Ng,
    bool Connected, bool Muted, int SkewSec, DateTimeOffset? DownUntil,
    OpcUaSnapshot? OpcUa = null);

public sealed record OpcUaSnapshot(string Endpoint, bool Running, double PeakTemp, bool SensorOk, double DriftPerMin);
