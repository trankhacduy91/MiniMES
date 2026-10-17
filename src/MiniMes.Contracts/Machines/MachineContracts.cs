using System.Text.Json;
using System.Text.Json.Serialization;

namespace MiniMes.Contracts.Machines;

// Spec M4 2.3: the data contract between machines (simulator, edge gateway M4b) and the MES ingest.
// Shared by both ends: changing the contract breaks the compile on both sides instead of breaking at run time.

// What a machine reports. Offline is never sent by a machine: the server infers it (no heartbeat, LWT).
public enum MachineState { Running, Idle, Down, Setup, PlannedStop, Offline }

// [v2] OPC UA status (Modbus: Good when the read worked). Direct MQTT machines are always Good.
// Bad values are stored but never used for counter deltas or SPC.
public enum DataQuality { Good, Bad, Uncertain }

// One JSON schema for every kind; fields that do not apply to the topic are null.
//   state      State                         QoS 1, retained
//   count      Counter (cumulative), Good/NgDelta (machine's own split, only for cross-checking)   QoS 1
//   alarm      AlarmCode (null = alarm cleared)                   QoS 1
//   heartbeat  State (lets the server recover a machine from Offline)   QoS 0, every 5 s
//   process/{tag}  Tag, Value, Quality [v2]                       QoS 1
// Ts is UTC ("Z"); Machine must equal the {machine} level of the topic.
public sealed record MachineMessage(
    Guid MsgId,
    string Machine,
    DateTimeOffset Ts,
    MachineState? State = null,
    long? Counter = null,
    int? GoodDelta = null,
    int? NgDelta = null,
    string? AlarmCode = null,
    string? Tag = null,                          // [v2] process/{tag} only
    double? Value = null,
    DataQuality Quality = DataQuality.Good,      // [v2]
    string Source = MachineSources.Mqtt);        // [v2] diagnosis only: the ingest treats every source the same

public static class MachineSources
{
    public const string Mqtt = "mqtt";
    public const string OpcUa = "opcua";
    public const string Modbus = "modbus";
}

public static class MachineKinds
{
    public const string State = "state";
    public const string Count = "count";
    public const string Alarm = "alarm";
    public const string Heartbeat = "heartbeat";
    public const string Status = "status";       // raw "online" / "offline" (connect, LWT), retained (step 5)
    public const string Process = "process";     // [v2] minimes/.../{machine}/process/{tag}
}

// Kind = the last level (state, count...), or "process" with Tag set for the 7-level process topics.
public readonly record struct ParsedTopic(string Site, string Area, string Line, string Machine, string Kind, string? Tag);

// [v2] Unified Namespace (ISA-95): minimes/{site}/{area}/{line}/{machine}/{kind}, one tree for the whole plant.
public static class MachineTopic
{
    public const string Root = "minimes";

    // Ingest subscriptions: every 6-level topic, and every process tag (7 levels).
    public const string AllMachineKinds = "minimes/+/+/+/+/+";
    public const string AllProcessTags = "minimes/+/+/+/+/process/+";

    public static string Build(string site, string area, string line, string machine, string kind)
        => $"{Root}/{site}/{area}/{line}/{machine}/{kind}";

    public static string BuildProcess(string site, string area, string line, string machine, string tag)
        => $"{Root}/{site}/{area}/{line}/{machine}/{MachineKinds.Process}/{tag}";

    // false = not a machine topic of this plant (wrong root, wrong depth, empty level): the ingest drops and logs it.
    public static bool TryParse(string topic, out ParsedTopic parsed)
    {
        parsed = default;
        var p = topic.Split('/');
        if (p[0] != Root || p.Any(string.IsNullOrWhiteSpace)) return false;
        if (p.Length == 6 && p[5] != MachineKinds.Process)
        {
            parsed = new ParsedTopic(p[1], p[2], p[3], p[4], p[5], null);
            return true;
        }
        if (p.Length == 7 && p[5] == MachineKinds.Process)
        {
            parsed = new ParsedTopic(p[1], p[2], p[3], p[4], MachineKinds.Process, p[6]);
            return true;
        }
        return false;
    }
}

public static class MachineJson
{
    // camelCase, enums as text ("Running"), case-insensitive reading: one serializer setting on both ends.
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };
}
