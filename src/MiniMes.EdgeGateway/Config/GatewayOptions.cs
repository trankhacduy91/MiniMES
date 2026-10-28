using MiniMes.EdgeGateway.Drivers.Modbus;

namespace MiniMes.EdgeGateway.Config;

// Spec M4b 2.6: gateway.tags.json. The tag configuration lives in the gateway, not in the MES database (rule G2):
// the gateway must start and keep reading the machines while the MES or the network is down.
public sealed class GatewayOptions
{
    public GatewayIdentity Gateway { get; set; } = new();
    public GatewayMqttOptions Mqtt { get; set; } = new();
    public OpcUaClientOptions OpcUa { get; set; } = new();
    public List<DeviceOptions> Devices { get; set; } = [];
}

// Where the gateway's machines sit in the Unified Namespace (minimes/{site}/{area}/{line}/...): must match the seeded
// master data, like the simulator's machines.json.
public sealed class GatewayIdentity
{
    public string Id { get; set; } = "";
    public string Site { get; set; } = "";
    public string Area { get; set; } = "";
    public string Line { get; set; } = "";
}

// The broker of the MES (M4). ClientId must be unique per gateway: two clients with one id kick each other off.
public sealed class GatewayMqttOptions
{
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 1883;
    public string ClientId { get; set; } = "";
}

public sealed class OpcUaClientOptions
{
    // null = only in Development (Program.cs). Accepting any server certificate in production is a security hole
    // (spec 2.3, trap 4): there the server's certificate is copied into pki/trusted by hand.
    public bool? AutoAcceptUntrustedCertificates { get; set; }
    public string PkiPath { get; set; } = "pki";   // relative to the exe folder; a fixed place, mounted as a volume in Docker
    public int KeepAliveMs { get; set; } = 2000;    // a dead server is noticed after about two missed keep-alives
}

public static class Protocols
{
    public const string OpcUa = "opcua";
    public const string Modbus = "modbus";
    public static readonly string[] Supported = [OpcUa, Modbus];
}

// One machine read by the gateway. Machine = Machine.MqttTopicKey of the MES (rule G3): a wrong code is data lost
// silently, because the ingest drops messages of unknown machines.
public sealed class DeviceOptions
{
    public string Machine { get; set; } = "";
    public string Protocol { get; set; } = "";

    // OPC UA
    public string Endpoint { get; set; } = "";
    public int PublishingMs { get; set; } = 1000;   // the server sends the gathered changes this often

    // Modbus TCP: one block of holding registers (FC03) read every PollMs; tags are offsets inside the block.
    public string Host { get; set; } = "";
    public int Port { get; set; } = 502;
    public byte UnitId { get; set; } = 1;
    public int PollMs { get; set; } = 1000;          // 500-1000 ms is plenty for a MES; faster only loads the PLC
    public ushort StartAddress { get; set; }         // 0-based wire address: "40001" in the manual = 0
    public ushort Count { get; set; }                // registers in the block (one request, at most 125)
    public int TimeoutMs { get; set; } = 2000;       // a dead slave must not freeze the poll loop

    public List<TagOptions> Tags { get; set; } = [];
}

public static class TagKinds
{
    public const string Count = "count";
    public const string State = "state";
    public const string Alarm = "alarm";
    public const string Process = "process";
    public static readonly string[] All = [Count, State, Alarm, Process];
}

public sealed class TagOptions
{
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "";

    // OPC UA: "nsu=<namespace URI>;s=..." is preferred over "ns=2;s=...": the index can change with the server's
    // firmware, the URI cannot (both work).
    public string NodeId { get; set; } = "";
    public int SamplingMs { get; set; } = 1000;     // how often the server samples the source (<= publishing interval)
    public double? Deadband { get; set; }           // absolute; the server drops smaller changes (rule G10)

    // Modbus: where the tag sits in the block and how its registers make a number.
    public int Offset { get; set; }
    public string Type { get; set; } = RegisterTypes.UInt16;
    public WordOrder WordOrder { get; set; } = WordOrder.ABCD;   // 32-bit types only; the manual rarely says which
    public bool ZeroMeansNone { get; set; }                      // alarm: register 0 = no alarm

    // state: the PLC's code -> MachineState name of the M4 contract ("1": "Running").
    public Dictionary<string, string> Map { get; set; } = [];
}
