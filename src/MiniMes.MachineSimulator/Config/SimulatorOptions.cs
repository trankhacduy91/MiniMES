namespace MiniMes.MachineSimulator.Config;

// Spec M4 2.8: machines.json. Codes must match the seeded master data (site, area, line, Machine.MqttTopicKey):
// the ingest drops messages of machines it does not know.
public sealed class SimulatorOptions
{
    public string Site { get; set; } = "";
    public string Area { get; set; } = "";
    public int Seed { get; set; } = 42;               // same seed, same demo: a bug seen once can be replayed
    public MqttOptions Mqtt { get; set; } = new();
    public List<MachineOptions> Machines { get; set; } = [];

    public IEnumerable<string> Validate()
    {
        if (string.IsNullOrWhiteSpace(Site) || string.IsNullOrWhiteSpace(Area)) yield return "site and area are required.";
        if (Machines.Count == 0) yield return "at least one machine is required.";
        if (Mqtt.HeartbeatSec <= 0) yield return "mqtt.heartbeatSec must be > 0.";
        foreach (var dup in Machines.GroupBy(m => m.Code, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            yield return $"machine {dup.Key} is listed twice.";
        foreach (var m in Machines)
        {
            if (string.IsNullOrWhiteSpace(m.Code) || string.IsNullOrWhiteSpace(m.Line)) yield return "every machine needs code and line.";
            if (m.CycleTimeSec <= 0) yield return $"{m.Code}: cycleTimeSec must be > 0.";
            if (m.DownDurationSec is not [var min, var max] || min <= 0 || max < min)
                yield return $"{m.Code}: downDurationSec must be [min, max] seconds.";
            if (m.AlarmCodes.Length == 0) yield return $"{m.Code}: alarmCodes needs at least one code.";
            if (m.NgProb is < 0 or > 1 || m.DownProbPerCycle is < 0 or > 1) yield return $"{m.Code}: probabilities must be in 0..1.";
        }
    }
}

public sealed class MqttOptions
{
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 1883;
    public int HeartbeatSec { get; set; } = 5;
}

public sealed class MachineOptions
{
    public string Code { get; set; } = "";
    public string Line { get; set; } = "";
    public double CycleTimeSec { get; set; } = 10;
    public long InitialCounter { get; set; }
    public double DownProbPerCycle { get; set; }
    // Arrays have no default here: the configuration binder APPENDS the JSON items to an initialised array
    // ([30, 180] + [30, 180] = 4 items), it does not replace it.
    public int[] DownDurationSec { get; set; } = [];
    public double NgProb { get; set; }
    public int ClockSkewSec { get; set; }              // a PLC whose clock is off; also settable by the "skew" command
    public string[] AlarmCodes { get; set; } = [];        // one is picked when the machine goes Down by itself
}
