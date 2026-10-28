using System.Text.Json;
using MiniMes.Application.Machines;
using MiniMes.Contracts.Machines;

namespace MiniMes.UnitTests.Machines;

// [M4b] Spec 2.8: the MES side of the gateway status (the "_sys" branch of the ingest).
public class GatewayStatusStoreTests
{
    private static readonly DateTime T0 = new(2026, 10, 28, 8, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("minimes/VN-BN01/_sys/gateway/GW01/status", true)]
    [InlineData("minimes/VN-BN01/SMT-ASSY/L1/L1-GLUE-01/status", false)]   // a machine's status: the normal path
    [InlineData("minimes/VN-BN01/_sys/gateway/GW01/heartbeat", false)]
    [InlineData("minimes/VN-BN01/_sys/broker/GW01/status", false)]
    [InlineData("other/VN-BN01/_sys/gateway/GW01/status", false)]
    [InlineData("minimes/VN-BN01/_sys/gateway//status", false)]
    public void Only_the_gateway_status_topic_takes_the_sys_branch(string topic, bool isGateway)
    {
        Assert.Equal(isGateway, GatewayTopic.TryParseStatus(topic, out var site, out var id));
        if (isGateway) Assert.Equal(("VN-BN01", "GW01"), (site, id));
        Assert.Equal("minimes/VN-BN01/_sys/gateway/GW01/status", GatewayTopic.Status("VN-BN01", "GW01"));
    }

    [Fact]
    public void Online_status_is_read_with_its_queue_and_devices()
    {
        var payload = Json(new GatewayStatusMessage("GW01", "online", 61, 50, 83.6,
            new Dictionary<string, string> { ["L1-REFLOW-01"] = "disconnected", ["L1-GLUE-01"] = "connected" }, T0));

        var (status, error) = GatewayStatusStore.Read("VN-BN01", "GW01", payload, T0, previous: null);

        Assert.Null(error);
        Assert.Equal(("GW01", "VN-BN01", "online", 61L, 50L, 83.6), (status!.Gateway, status.Site, status.State, status.QueueLength, status.DroppedCount, status.OldestAgeSec));
        Assert.Equal("disconnected", status.Devices["L1-REFLOW-01"]);
        Assert.Equal(T0, status.ReceivedAt);
    }

    // The LWT says only {"gateway":"GW01","state":"offline"}: the card keeps its machines, all disconnected.
    [Fact]
    public void Lwt_keeps_the_devices_of_the_last_status_all_disconnected()
    {
        var store = new GatewayStatusStore();
        var online = GatewayStatusStore.Read("VN-BN01", "GW01", Json(new GatewayStatusMessage("GW01", "online",
            Devices: new Dictionary<string, string> { ["L1-REFLOW-01"] = "connected", ["L1-GLUE-01"] = "connected" })), T0, null).Status!;
        store.Set(online);

        var (lwt, _) = GatewayStatusStore.Read("VN-BN01", "GW01", """{"gateway":"GW01","state":"offline"}""", T0.AddSeconds(1), store.Get("GW01"));

        Assert.Equal("offline", lwt!.State);
        Assert.Equal(["L1-REFLOW-01", "L1-GLUE-01"], lwt.Devices.Keys);
        Assert.All(lwt.Devices.Values, v => Assert.Equal("disconnected", v));
    }

    [Theory]
    [InlineData("not json", "invalid JSON")]
    [InlineData("""{"gateway":"GW01","state":"sleeping"}""", "state must be")]
    [InlineData("""{"gateway":"GW02","state":"online"}""", "does not match the topic")]
    public void Bad_status_is_dropped_with_a_reason(string payload, string reason)
    {
        var (status, error) = GatewayStatusStore.Read("VN-BN01", "GW01", payload, T0, null);

        Assert.Null(status);
        Assert.Contains(reason, error);
    }

    [Fact]
    public void A_line_sees_the_gateways_reading_its_machines()
    {
        var store = new GatewayStatusStore();
        store.Set(Status("GW01", "L1-REFLOW-01", "L1-GLUE-01"));
        store.Set(Status("GW02", "L2-PRESS-01"));

        Assert.Equal(["GW01"], store.ForMachines(["L1-SMT-01", "L1-GLUE-01"]).Select(g => g.Gateway));
        Assert.Empty(store.ForMachines(["L3-SMT-01"]));
    }

    private static string Json(GatewayStatusMessage m) => JsonSerializer.Serialize(m, MachineJson.Options);

    private static Contracts.Realtime.GatewayStatusDto Status(string id, params string[] machines)
        => new(id, "VN-BN01", "online", 0, 0, 0, machines.ToDictionary(m => m, _ => "connected"), T0);
}
