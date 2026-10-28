using MiniMes.EdgeGateway.Config;

namespace MiniMes.UnitTests.Gateway;

// Spec M4b step 2: a wrong gateway.tags.json must stop the gateway at start (empty endpoint, unknown protocol,
// duplicate tag...), with a message that says which machine and which tag.
public class GatewayOptionsValidatorTests
{
    [Fact]
    public void The_shipped_configuration_is_valid()
    {
        Assert.Empty(GatewayOptionsValidator.Errors(Valid()));
    }

    public static TheoryData<string, Action<GatewayOptions>, string> Broken => new()
    {
        { "empty endpoint", o => o.Devices[0].Endpoint = "", "endpoint must be an opc.tcp:// URL" },
        { "http endpoint", o => o.Devices[0].Endpoint = "http://localhost:4840", "endpoint must be an opc.tcp:// URL" },
        { "unknown protocol", o => o.Devices[0].Protocol = "s7", "protocol 's7' is not supported" },
        { "duplicate tag (any case)", o => o.Devices[0].Tags[1].Name = "PEAKTEMP", "tag peakTemp is listed twice" },
        { "duplicate machine", o => o.Devices.Add(Device()), "machine L1-REFLOW-01 is listed twice" },
        { "no machine", o => o.Devices[0].Machine = "", "every device needs machine" },
        { "no gateway id", o => o.Gateway.Id = "", "gateway.id, site, area and line are required" },
        { "no device", o => o.Devices.Clear(), "at least one device is required" },
        { "no tags", o => o.Devices[0].Tags.Clear(), "L1-REFLOW-01: no tags" },
        { "unknown kind", o => o.Devices[0].Tags[0].Kind = "temperature", "kind 'temperature' is not one of" },
        { "two count tags", o => o.Devices[0].Tags[0].Kind = "count", "only one count tag per machine" },
        { "missing nodeId", o => o.Devices[0].Tags[0].NodeId = "", "peakTemp: nodeId is required" },
        { "invalid nodeId", o => o.Devices[0].Tags[0].NodeId = "Reflow.PeakTemp", "nodeId 'Reflow.PeakTemp' is not valid" },
        { "sampling slower than publishing", o => o.Devices[0].Tags[0].SamplingMs = 5000, "samplingMs must be <= publishingMs" },
        { "negative deadband", o => o.Devices[0].Tags[0].Deadband = -1, "deadband must be >= 0" },
        { "state without map", o => o.Devices[0].Tags[2].Map.Clear(), "a state tag needs map" },
        { "map to unknown state", o => o.Devices[0].Tags[2].Map["6"] = "Broken", "map 6 -> 'Broken' is not a machine state" },
        { "map to Offline", o => o.Devices[0].Tags[2].Map["6"] = "Offline", "map 6 -> 'Offline' is not a machine state" },
        { "map to a number", o => o.Devices[0].Tags[2].Map["6"] = "2", "map 6 -> '2' is not a machine state" },
        // Modbus (step 3): Devices[1] is the glue dispenser.
        { "modbus without host", o => o.Devices[1].Host = "", "L1-GLUE-01: host is required" },
        { "modbus bad port", o => o.Devices[1].Port = 0, "port must be 1..65535" },
        { "modbus unit id", o => o.Devices[1].UnitId = 248, "unitId must be 0..247" },
        { "polled too fast", o => o.Devices[1].PollMs = 50, "pollMs must be >= 100" },
        { "block too big", o => o.Devices[1].Count = 126, "count must be 1..125" },
        { "empty block", o => o.Devices[1].Count = 0, "count must be 1..125" },
        { "uint32 past the end", o => o.Devices[1].Tags[0].Offset = 3, "offset 3 (uint32) is outside the block of 4 registers" },
        { "uint16 past the end", o => o.Devices[1].Tags[2].Offset = 4, "offset 4 (uint16) is outside the block of 4 registers" },
        { "unknown type", o => o.Devices[1].Tags[0].Type = "double", "type 'double' is not one of" },
        { "nodeId on a modbus tag", o => o.Devices[1].Tags[0].NodeId = "ns=2;s=X", "a Modbus tag uses offset" },
    };

    [Theory]
    [MemberData(nameof(Broken))]
    public void A_broken_configuration_is_refused(string _, Action<GatewayOptions> breakIt, string expected)
    {
        var options = Valid();
        breakIt(options);

        var errors = GatewayOptionsValidator.Errors(options).ToList();

        Assert.Contains(errors, e => e.Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public void Both_nodeId_forms_are_accepted()
    {
        var options = Valid();
        options.Devices[0].Tags[0].NodeId = "ns=2;s=Reflow.PeakTemp";
        options.Devices[0].Tags[1].NodeId = "ns=2;i=1001";

        Assert.Empty(GatewayOptionsValidator.Errors(options));
    }

    [Fact]
    public void Validate_fails_with_every_error_listed()
    {
        var options = Valid();
        options.Devices[0].Endpoint = "";
        options.Devices[0].Tags[1].Name = "peakTemp";

        var result = new GatewayOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Equal(2, result.Failures!.Count());
    }

    // Same shape as src/MiniMes.EdgeGateway/gateway.tags.json.
    private static GatewayOptions Valid() => new()
    {
        Gateway = new GatewayIdentity { Id = "GW01", Site = "VN-BN01", Area = "SMT-ASSY", Line = "L1" },
        Devices = [Device(), Glue()],
    };

    private static DeviceOptions Glue() => new()
    {
        Machine = "L1-GLUE-01",
        Protocol = "modbus",
        Host = "localhost",
        Port = 5020,
        UnitId = 1,
        PollMs = 1000,
        StartAddress = 0,
        Count = 4,
        Tags =
        [
            new() { Name = "counter", Kind = "count", Offset = 0, Type = "uint32", WordOrder = MiniMes.EdgeGateway.Drivers.Modbus.WordOrder.CDAB },
            new() { Name = "state", Kind = "state", Offset = 2, Type = "uint16", Map = new() { ["1"] = "Running", ["3"] = "Down" } },
            new() { Name = "alarm", Kind = "alarm", Offset = 3, Type = "uint16", ZeroMeansNone = true },
        ],
    };

    private static DeviceOptions Device() => new()
    {
        Machine = "L1-REFLOW-01",
        Protocol = "opcua",
        Endpoint = "opc.tcp://localhost:4840",
        PublishingMs = 1000,
        Tags =
        [
            new() { Name = "peakTemp", Kind = "process", NodeId = "nsu=urn:minimes:simulator:reflow;s=Reflow.PeakTemp", SamplingMs = 500, Deadband = 0.5 },
            new() { Name = "counter", Kind = "count", NodeId = "nsu=urn:minimes:simulator:reflow;s=Reflow.Counter", SamplingMs = 1000 },
            new()
            {
                Name = "state", Kind = "state", NodeId = "nsu=urn:minimes:simulator:reflow;s=Reflow.State", SamplingMs = 1000,
                Map = new() { ["1"] = "Running", ["2"] = "Idle", ["3"] = "Down", ["4"] = "Setup", ["5"] = "PlannedStop" },
            },
        ],
    };
}
