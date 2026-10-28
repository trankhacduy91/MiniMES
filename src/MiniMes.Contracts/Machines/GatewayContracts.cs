namespace MiniMes.Contracts.Machines;

// [M4b] Spec 2.8: the edge gateway's own health, on minimes/{site}/_sys/gateway/{id}/status (retained, QoS 1).
// Published every 30 s and at every change while it runs; the broker publishes the LWT {"state":"offline"} when the
// gateway dies without saying goodbye. The ingest takes this branch BEFORE looking up machines: "_sys" is not an area.
// Devices: machine code -> "connected" / "disconnected" (can the gateway read that PLC right now).
public sealed record GatewayStatusMessage(
    string Gateway,
    string State,
    long QueueLength = 0,
    long DroppedCount = 0,
    double OldestAgeSec = 0,
    IReadOnlyDictionary<string, string>? Devices = null,
    DateTimeOffset? Ts = null);

public static class GatewayStates
{
    public const string Online = "online";
    public const string Offline = "offline";
    public const string Connected = "connected";
    public const string Disconnected = "disconnected";
}

public static class GatewayTopic
{
    public const string SystemArea = "_sys";
    public const string GatewayLevel = "gateway";

    public static string Status(string site, string gatewayId)
        => $"{MachineTopic.Root}/{site}/{SystemArea}/{GatewayLevel}/{gatewayId}/{MachineKinds.Status}";

    // minimes/{site}/_sys/gateway/{id}/status -> (site, id). It also matches the machine wildcard minimes/+/+/+/+/+,
    // so the ingest receives it on its existing subscription.
    public static bool TryParseStatus(string topic, out string site, out string gatewayId)
    {
        (site, gatewayId) = ("", "");
        var p = topic.Split('/');
        if (p.Length != 6 || p[0] != MachineTopic.Root || p[2] != SystemArea || p[3] != GatewayLevel || p[5] != MachineKinds.Status
            || string.IsNullOrWhiteSpace(p[1]) || string.IsNullOrWhiteSpace(p[4]))
            return false;
        (site, gatewayId) = (p[1], p[4]);
        return true;
    }
}
