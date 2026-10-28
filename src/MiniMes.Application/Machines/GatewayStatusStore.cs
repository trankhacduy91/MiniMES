using System.Collections.Concurrent;
using System.Text.Json;
using MiniMes.Contracts.Machines;
using MiniMes.Contracts.Realtime;

namespace MiniMes.Application.Machines;

// [M4b] Spec 2.8: the last status of every edge gateway, in RAM. Not stored in the DB: the status topic is retained,
// so after an API restart the broker hands the latest one back on subscribe.
// The machines behind a gateway are not touched here: a dead gateway stops their heartbeats and the M4 watchdog
// makes them Offline after 30 s; a lost PLC is reported by the gateway on the machine's own status topic.
public sealed class GatewayStatusStore
{
    private readonly ConcurrentDictionary<string, GatewayStatusDto> _gateways = new(StringComparer.OrdinalIgnoreCase);

    // Pure: topic + payload -> the dashboard's DTO, or why it was dropped. The LWT carries only the state: the devices
    // of the previous status are kept, so the card still knows which machines this gateway was reading.
    public static (GatewayStatusDto? Status, string? Error) Read(string site, string gatewayId, string payload, DateTime receivedAt,
        GatewayStatusDto? previous)
    {
        GatewayStatusMessage? msg;
        try { msg = JsonSerializer.Deserialize<GatewayStatusMessage>(payload, MachineJson.Options); }
        catch (JsonException ex) { return (null, $"invalid JSON ({ex.Message})"); }
        if (msg is null) return (null, "empty payload");
        if (msg.State is not (GatewayStates.Online or GatewayStates.Offline)) return (null, $"state must be '{GatewayStates.Online}' or '{GatewayStates.Offline}'");
        if (!string.Equals(msg.Gateway, gatewayId, StringComparison.OrdinalIgnoreCase)) return (null, $"gateway '{msg.Gateway}' does not match the topic");

        var devices = msg.Devices ?? previous?.Devices ?? new Dictionary<string, string>();
        if (msg.State == GatewayStates.Offline)
            // Dead gateway: it reads nothing any more, whatever it last said.
            devices = devices.ToDictionary(d => d.Key, _ => GatewayStates.Disconnected);
        return (new GatewayStatusDto(gatewayId, site, msg.State, msg.QueueLength, msg.DroppedCount, msg.OldestAgeSec,
            devices, receivedAt), null);
    }

    public GatewayStatusDto? Get(string gatewayId) => _gateways.GetValueOrDefault(gatewayId);

    public void Set(GatewayStatusDto status) => _gateways[status.Gateway] = status;

    // The gateways reading at least one of these machines (a line's dashboard).
    public IReadOnlyList<GatewayStatusDto> ForMachines(IEnumerable<string> machineCodes)
    {
        var codes = machineCodes.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return _gateways.Values.Where(g => g.Devices.Keys.Any(codes.Contains)).OrderBy(g => g.Gateway).ToList();
    }
}
