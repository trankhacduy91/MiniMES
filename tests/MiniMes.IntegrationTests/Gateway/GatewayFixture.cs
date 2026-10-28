extern alias gateway;
extern alias simulator;

using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MiniMes.IntegrationTests.Machines;
using MQTTnet;
using MQTTnet.Protocol;
using GatewayHost = gateway::MiniMes.EdgeGateway.GatewayHost;
using GlueMachineSlave = simulator::MiniMes.MachineSimulator.Modbus.GlueMachineSlave;
using ModbusOptions = simulator::MiniMes.MachineSimulator.Config.ModbusOptions;
using OpcUaOptions = simulator::MiniMes.MachineSimulator.Config.OpcUaOptions;
using ReflowServer = simulator::MiniMes.MachineSimulator.OpcUa.ReflowServer;
using ReflowValues = simulator::MiniMes.MachineSimulator.OpcUa.ReflowValues;
using SimulatorTelemetry = simulator::MiniMes.MachineSimulator.OpcUa.HostTelemetry;

namespace MiniMes.IntegrationTests.Gateway;

// Spec M4b step 7: the plant side in one process, against a real Mosquitto container:
//   simulator's OPC UA server (reflow oven) + Modbus slave (glue dispenser)  ->  the gateway's real host
//   (drivers, normalizer, SQLite outbox, forwarder)  ->  Mosquitto  ->  a subscriber standing for the MES.
// The subscriber keeps a persistent session (clean session off, fixed client id), like the MES ingest: while it is
// away (broker restart) the broker keeps its QoS 1 messages, so "nothing lost" can be checked end to end.
public sealed class GatewayFixture : IAsyncLifetime
{
    public const string Site = "VN-BN01", Area = "SMT-ASSY", Line = "L1";
    public const string Oven = "L1-REFLOW-01", Glue = "L1-GLUE-01";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "minimes-gateway-e2e", Guid.NewGuid().ToString("N"));
    private readonly IMqttClient _subscriber = new MqttClientFactory().CreateMqttClient();
    private readonly CancellationTokenSource _stop = new();
    private IHost _gateway = default!;
    private ReflowServer _oven = default!;
    private Task _keepSubscribed = Task.CompletedTask;

    public MosquittoFixture Broker { get; } = new();
    public GlueMachineSlave GlueSlave { get; private set; } = default!;
    public ConcurrentQueue<(string Topic, string Payload, bool Retain)> Received { get; } = new();

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        await Broker.InitializeAsync();

        var modbusPort = FreePort();
        GlueSlave = new GlueMachineSlave(Glue, new ModbusOptions { Port = modbusPort, UnitId = 1 }, NullLogger.Instance);
        GlueSlave.Start();
        GlueSlave.Publish(counter: 100_000, state: 1, alarm: 0);

        var opcUaPort = FreePort();
        _oven = new ReflowServer(Oven, new OpcUaOptions { Host = "localhost", Port = opcUaPort },
            new SimulatorTelemetry(NullLoggerFactory.Instance), NullLogger.Instance);
        await _oven.StartAsync();
        _oven.Publish(new ReflowValues(245.0, SensorOk: true, Counter: 8000, State: 1), DateTime.UtcNow);

        _keepSubscribed = KeepSubscribedAsync(_stop.Token);
        await WaitUntilAsync(() => _subscriber.IsConnected, TimeSpan.FromSeconds(20), "subscriber connected");

        var config = Path.Combine(_dir, "gateway.tags.json");
        await File.WriteAllTextAsync(config, GatewayConfig(opcUaPort, modbusPort));
        var (builder, _) = GatewayHost.CreateBuilder(["--config", config, "--environment", "Development"]);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);   // the test output, not the gateway's chatter
        _gateway = builder.Build();
        await _gateway.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _gateway.StopAsync();
        _gateway.Dispose();
        await _stop.CancelAsync();
        try { await _keepSubscribed; } catch (OperationCanceledException) { }
        _subscriber.Dispose();
        await _oven.StopAsync();
        GlueSlave.Stop();
        await Broker.DisposeAsync();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    public static string Topic(string machine, string kind) => $"minimes/{Site}/{Area}/{Line}/{machine}/{kind}";

    public static async Task<T> WaitForAsync<T>(Func<T> read, Func<T, bool> done, TimeSpan timeout, string what)
    {
        var until = DateTime.UtcNow + timeout;
        var value = read();
        while (!done(value) && DateTime.UtcNow < until)
        {
            await Task.Delay(200);
            value = read();
        }
        Assert.True(done(value), $"{what}: not reached in {timeout.TotalSeconds:0} s");
        return value;
    }

    private static Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, string what)
        => WaitForAsync(condition, ok => ok, timeout, what);

    // MQTTnet does not reconnect by itself: a loop, like the MES ingest. Same client id + clean session off = the
    // broker keeps the subscription and the queued messages while this client is away.
    private async Task KeepSubscribedAsync(CancellationToken ct)
    {
        _subscriber.ApplicationMessageReceivedAsync += e =>
        {
            Received.Enqueue((e.ApplicationMessage.Topic, e.ApplicationMessage.ConvertPayloadToString(), e.ApplicationMessage.Retain));
            return Task.CompletedTask;
        };
        var options = new MqttClientOptionsBuilder()
            .WithTcpServer("localhost", Broker.Port)
            .WithClientId("e2e-mes-" + Guid.NewGuid().ToString("N")[..8])
            .WithCleanSession(false)
            .WithTimeout(TimeSpan.FromSeconds(3))
            .Build();
        var subscribed = false;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!_subscriber.IsConnected)
                {
                    var result = await _subscriber.ConnectAsync(options, ct);
                    if (!subscribed || !result.IsSessionPresent)
                    {
                        await _subscriber.SubscribeAsync(new MqttClientSubscribeOptionsBuilder()
                            .WithTopicFilter(f => f.WithTopic("minimes/#").WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce))
                            .Build(), ct);
                        subscribed = true;
                    }
                }
                await Task.Delay(300, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception) { await Task.Delay(500, ct).ContinueWith(_ => { }, CancellationToken.None); }
        }
    }

    // Poll 200 ms (instead of 1 s) and an outbox in the test folder; otherwise the shipped gateway.tags.json.
    private string GatewayConfig(int opcUaPort, int modbusPort) => JsonSerializer.Serialize(new
    {
        gateway = new { id = "GW-E2E", site = Site, area = Area, line = Line },
        mqtt = new { host = "localhost", port = Broker.Port, clientId = "minimes-gw-e2e-" + Guid.NewGuid().ToString("N")[..8] },
        queue = new { path = Path.Combine(_dir, "outbox.db"), maxRows = 500_000, maxAgeDays = 7 },
        opcUa = new { pkiPath = Path.Combine(_dir, "pki"), keepAliveMs = 1000 },
        devices = new object[]
        {
            new
            {
                machine = Oven, protocol = "opcua", endpoint = $"opc.tcp://localhost:{opcUaPort}", publishingMs = 500,
                tags = new object[]
                {
                    new { name = "peakTemp", kind = "process", nodeId = "nsu=urn:minimes:simulator:reflow;s=Reflow.PeakTemp", samplingMs = 250, deadband = 0.5 },
                    new { name = "counter", kind = "count", nodeId = "nsu=urn:minimes:simulator:reflow;s=Reflow.Counter", samplingMs = 250 },
                    new { name = "state", kind = "state", nodeId = "nsu=urn:minimes:simulator:reflow;s=Reflow.State", samplingMs = 250, map = StateMap },
                },
            },
            new
            {
                machine = Glue, protocol = "modbus", host = "localhost", port = modbusPort, unitId = 1,
                pollMs = 200, timeoutMs = 1000, startAddress = 0, count = 4,
                tags = new object[]
                {
                    new { name = "counter", kind = "count", offset = 0, type = "uint32", wordOrder = "CDAB" },
                    new { name = "state", kind = "state", offset = 2, type = "uint16", map = StateMap },
                    new { name = "alarm", kind = "alarm", offset = 3, type = "uint16", zeroMeansNone = true },
                },
            },
        },
    });

    private static readonly Dictionary<string, string> StateMap =
        new() { ["1"] = "Running", ["2"] = "Idle", ["3"] = "Down", ["4"] = "Setup", ["5"] = "PlannedStop" };

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}

[CollectionDefinition(Name)]
public sealed class GatewayCollection : ICollectionFixture<GatewayFixture>
{
    public const string Name = "Gateway";
}
