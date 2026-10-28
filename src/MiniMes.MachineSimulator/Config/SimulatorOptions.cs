using MiniMes.MachineSimulator.Modbus;

namespace MiniMes.MachineSimulator.Config;

// Spec M4 2.8: machines.json. Codes must match the seeded master data (site, area, line, Machine.MqttTopicKey):
// the ingest drops messages of machines it does not know.
public sealed class SimulatorOptions
{
    public string Site { get; set; } = "";
    public string Area { get; set; } = "";
    public int Seed { get; set; } = 42;               // same seed, same demo: a bug seen once can be replayed
    public MqttOptions Mqtt { get; set; } = new();
    public ApiOptions? Api { get; set; }              // station role (step 9); without it the machines only talk MQTT
    public List<MachineOptions> Machines { get; set; } = [];
    // Manual stations (Assembly, Pack) scanned by the simulator only with --auto-operator; otherwise people use the kiosk.
    public List<AutoOperatorOptions> AutoOperators { get; set; } = [];

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
            if (m.OpcUa is { Port: < 1 or > 65535 }) yield return $"{m.Code}: opcua.port must be 1..65535.";
            if (m.Modbus is { Port: < 1 or > 65535 }) yield return $"{m.Code}: modbus.port must be 1..65535.";
            if (m.Modbus is { UnitId: < 1 or > 247 }) yield return $"{m.Code}: modbus.unitId must be 1..247.";
            if (!m.Mqtt && m.OpcUa is null && m.Modbus is null) yield return $"{m.Code}: mqtt false needs an opcua or modbus side, or nobody hears it.";
            if (!m.Mqtt && m.Station is not null) yield return $"{m.Code}: a station machine must speak MQTT.";
            // The Modbus alarm register holds the digits of the code (G301 -> 301): they must fit in 16 bits.
            if (m.Modbus is not null)
                foreach (var code in m.AlarmCodes.Append("E999").Where(c => GlueMachineSlave.AlarmRegister(c) is null))
                    yield return $"{m.Code}: alarm code {code} has no number 1..65535 for the Modbus alarm register.";
            if (m.Station is { } st)
            {
                if (string.IsNullOrWhiteSpace(st.Code)) yield return $"{m.Code}: station.code is required.";
                if (st.Mode == StationMode.AutoTest && st.DefectCodes.Length == 0)
                    yield return $"{m.Code}: station mode AutoTest needs defectCodes for its NG boards.";
            }
        }
        var ports = Machines.Where(m => m.OpcUa is not null).Select(m => (m.Code, m.OpcUa!.Port))
            .Concat(Machines.Where(m => m.Modbus is not null).Select(m => (m.Code, m.Modbus!.Port)));
        foreach (var dup in ports.GroupBy(p => p.Port).Where(g => g.Count() > 1))
            yield return $"port {dup.Key} is used twice ({string.Join(", ", dup.Select(p => p.Code))}).";
        foreach (var o in AutoOperators)
            if (string.IsNullOrWhiteSpace(o.Station) || o.CycleTimeSec <= 0) yield return "every autoOperator needs station and cycleTimeSec > 0.";
        if ((Machines.Any(m => m.Station is not null) || AutoOperators.Count > 0)
            && !Uri.TryCreate(Api?.BaseUrl, UriKind.Absolute, out _))
            yield return "api.baseUrl is required when a machine has a station or autoOperators are listed.";
    }
}

public sealed class MqttOptions
{
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 1883;
    public int HeartbeatSec { get; set; } = 5;
}

// Step 9: the API the station role calls, like the kiosk. The badge is a seeded Operator without a password
// (DevSeeder.SimulatorBadge): no secret in this file.
public sealed class ApiOptions
{
    public string BaseUrl { get; set; } = "";
    public string Badge { get; set; } = "SIM-STATION";
}

// Auto = always OK (a placement machine does not judge the board); AutoTest = the tester's verdict, NG with a defect.
public enum StationMode { Auto, AutoTest }

public sealed class StationOptions
{
    public string Code { get; set; } = "";
    public StationMode Mode { get; set; } = StationMode.Auto;
    public string[] DefectCodes { get; set; } = [];     // seeded codes; one is picked for each NG board
}

public sealed class AutoOperatorOptions
{
    public string Station { get; set; } = "";
    public double CycleTimeSec { get; set; } = 10;
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
    public StationOptions? Station { get; set; }          // step 9: this machine also scans each board at that station
    public OpcUaOptions? OpcUa { get; set; }              // M4b: the machine also shows its values on an OPC UA server
    public ModbusOptions? Modbus { get; set; }            // M4b: the machine also keeps its values in Modbus registers
    // false = an old machine without MQTT: the MES only hears it through the edge gateway (Modbus/OPC UA side).
    public bool Mqtt { get; set; } = true;
}

// M4b step 3: the glue dispenser as a Modbus TCP slave (registers in GlueMachineSlave). Listens on every interface.
public sealed class ModbusOptions
{
    public int Port { get; set; } = 5020;
    public byte UnitId { get; set; } = 1;
}

// M4b step 1: the reflow oven as an OPC UA server (PeakTemp, Counter, State). Host = the name clients use to reach it
// (localhost on the dev PC, the service name in Docker).
public sealed class OpcUaOptions
{
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 4840;
    public double PeakTempC { get; set; } = 245;          // set point; the reading is this + noise + drift
    public double NoiseC { get; set; } = 0.8;             // uniform +- noise of each reading
}
