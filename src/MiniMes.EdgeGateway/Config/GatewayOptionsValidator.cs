using Microsoft.Extensions.Options;
using MiniMes.Contracts.Machines;
using MiniMes.EdgeGateway.Drivers.Modbus;
using Opc.Ua;

namespace MiniMes.EdgeGateway.Config;

// Spec M4b step 2: a wrong configuration stops the gateway at start (ValidateOnStart) instead of running half blind.
// A gateway that starts with an empty endpoint or two tags of the same name loses data without anyone noticing.
public sealed class GatewayOptionsValidator : IValidateOptions<GatewayOptions>
{
    public ValidateOptionsResult Validate(string? name, GatewayOptions options)
    {
        var errors = Errors(options).ToList();
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }

    public static IEnumerable<string> Errors(GatewayOptions o)
    {
        var g = o.Gateway;
        if (new[] { g.Id, g.Site, g.Area, g.Line }.Any(string.IsNullOrWhiteSpace))
            yield return "gateway.id, site, area and line are required.";
        if (string.IsNullOrWhiteSpace(o.Mqtt.Host) || o.Mqtt.Port is < 1 or > 65535) yield return "mqtt.host and mqtt.port (1..65535) are required.";
        if (string.IsNullOrWhiteSpace(o.Mqtt.ClientId)) yield return "mqtt.clientId is required (unique per gateway, e.g. minimes-gw-GW01).";
        if (string.IsNullOrWhiteSpace(o.Queue.Path)) yield return "queue.path is required.";
        if (o.Queue.MaxRows < 1 || o.Queue.MaxAgeDays < 1) yield return "queue.maxRows and queue.maxAgeDays must be >= 1.";
        if (o.Devices.Count == 0) yield return "at least one device is required.";
        foreach (var dup in o.Devices.GroupBy(d => d.Machine, StringComparer.OrdinalIgnoreCase).Where(x => x.Count() > 1))
            yield return $"machine {dup.Key} is listed twice: two drivers reading one machine send everything twice.";

        foreach (var d in o.Devices)
        {
            var m = string.IsNullOrWhiteSpace(d.Machine) ? "(no machine)" : d.Machine;
            if (string.IsNullOrWhiteSpace(d.Machine)) yield return "every device needs machine (= Machine.MqttTopicKey).";
            if (!Protocols.Supported.Contains(d.Protocol))
            {
                yield return $"{m}: protocol '{d.Protocol}' is not supported (supported: {string.Join(", ", Protocols.Supported)}).";
                continue;
            }
            if (d.Tags.Count == 0) yield return $"{m}: no tags.";
            foreach (var dup in d.Tags.GroupBy(t => t.Name, StringComparer.OrdinalIgnoreCase).Where(x => x.Count() > 1))
                yield return $"{m}: tag {dup.Key} is listed twice.";
            foreach (var dup in d.Tags.Where(t => t.Kind != TagKinds.Process).GroupBy(t => t.Kind).Where(x => x.Count() > 1))
                yield return $"{m}: only one {dup.Key} tag per machine.";

            var protocolErrors = d.Protocol == Protocols.OpcUa ? OpcUaErrors(m, d) : ModbusErrors(m, d);
            foreach (var e in protocolErrors) yield return e;
            foreach (var t in d.Tags)
                foreach (var e in TagErrors(m, t)) yield return e;
        }
    }

    private static IEnumerable<string> OpcUaErrors(string m, DeviceOptions d)
    {
        if (!Uri.TryCreate(d.Endpoint, UriKind.Absolute, out var uri) || uri.Scheme != "opc.tcp")
            yield return $"{m}: endpoint must be an opc.tcp:// URL (was '{d.Endpoint}').";
        if (d.PublishingMs <= 0) yield return $"{m}: publishingMs must be > 0.";
        foreach (var t in d.Tags)
        {
            if (string.IsNullOrWhiteSpace(t.NodeId)) yield return $"{m}.{t.Name}: nodeId is required.";
            else if (!IsNodeId(t.NodeId)) yield return $"{m}.{t.Name}: nodeId '{t.NodeId}' is not valid (e.g. nsu=urn:...;s=Reflow.PeakTemp).";
            if (t.SamplingMs <= 0) yield return $"{m}.{t.Name}: samplingMs must be > 0.";
            if (t.SamplingMs > d.PublishingMs) yield return $"{m}.{t.Name}: samplingMs must be <= publishingMs ({d.PublishingMs}).";
        }
    }

    private static IEnumerable<string> ModbusErrors(string m, DeviceOptions d)
    {
        if (string.IsNullOrWhiteSpace(d.Host)) yield return $"{m}: host is required.";
        if (d.Port is < 1 or > 65535) yield return $"{m}: port must be 1..65535.";
        if (d.UnitId > 247) yield return $"{m}: unitId must be 0..247.";
        if (d.PollMs < 100) yield return $"{m}: pollMs must be >= 100 (a PLC polled too fast slows down).";
        if (d.TimeoutMs <= 0 || d.TimeoutMs >= d.PollMs * 10) yield return $"{m}: timeoutMs must be > 0 and well under 10 polls.";
        // FC03 returns at most 125 registers per request.
        if (d.Count is < 1 or > 125) yield return $"{m}: count must be 1..125 registers (one FC03 request).";
        foreach (var t in d.Tags)
        {
            if (!RegisterTypes.All.Contains(t.Type))
            {
                yield return $"{m}.{t.Name}: type '{t.Type}' is not one of {string.Join(", ", RegisterTypes.All)}.";
                continue;
            }
            if (t.Offset < 0 || t.Offset + RegisterTypes.Width(t.Type) > d.Count)
                yield return $"{m}.{t.Name}: offset {t.Offset} ({t.Type}) is outside the block of {d.Count} registers.";
            if (!string.IsNullOrWhiteSpace(t.NodeId)) yield return $"{m}.{t.Name}: nodeId is OPC UA; a Modbus tag uses offset.";
        }
    }

    private static IEnumerable<string> TagErrors(string m, TagOptions t)
    {
        if (string.IsNullOrWhiteSpace(t.Name)) yield return $"{m}: every tag needs a name.";
        if (!TagKinds.All.Contains(t.Kind)) yield return $"{m}.{t.Name}: kind '{t.Kind}' is not one of {string.Join(", ", TagKinds.All)}.";
        if (t.Deadband is < 0) yield return $"{m}.{t.Name}: deadband must be >= 0.";
        if (t.Kind == TagKinds.State)
        {
            if (t.Map.Count == 0) yield return $"{m}.{t.Name}: a state tag needs map (PLC code -> Running, Idle, Down, Setup, PlannedStop).";
            foreach (var (code, state) in t.Map)
                // Offline is the MES's conclusion (no heartbeat, status offline), never a value read from a machine.
                if (!Enum.TryParse<MachineState>(state, ignoreCase: false, out var s) || s == MachineState.Offline || int.TryParse(state, out _))
                    yield return $"{m}.{t.Name}: map {code} -> '{state}' is not a machine state.";
        }
    }

    private static bool IsNodeId(string text)
    {
        try { return !ExpandedNodeId.Parse(text).IsNull; }
        catch (ServiceResultException) { return false; }
        catch (ArgumentException) { return false; }
    }
}
