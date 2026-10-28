using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MiniMes.Contracts.Machines;
using MiniMes.EdgeGateway.Config;
using MiniMes.EdgeGateway.Mqtt;
using MiniMes.EdgeGateway.Queue;

namespace MiniMes.EdgeGateway.Health;

// Spec M4b 2.8: the gateway's own status, minimes/{site}/_sys/gateway/{id}/status, retained, QoS 1:
//   {"gateway":"GW01","state":"online","queueLength":0,"droppedCount":0,"oldestAgeSec":0,"devices":{...},"ts":"..."}
// Every 30 s, and at once after a (re)connect to the broker or a device connection change. Sent directly, not through
// the outbox: an old status replayed later would lie. The broker publishes the LWT ("offline") if the gateway dies;
// a clean stop publishes "offline" itself (a clean disconnect does not fire the LWT).
public sealed class GatewayStatusPublisher(
    GatewayOptions options,
    HealthPublisher health,
    SqliteOutbox outbox,
    GatewayMqttClient mqtt,
    TimeProvider clock,
    ILogger<GatewayStatusPublisher> log) : BackgroundService
{
    public static readonly TimeSpan Every = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim _wake = new(0, 1);

    // Publish now instead of at the next 30 s tick.
    public void Trigger()
    {
        if (_wake.CurrentCount == 0) try { _wake.Release(); } catch (SemaphoreFullException) { }
    }

    // Hooked before ExecuteAsync runs, and a first publish right away: the MQTT client starts earlier and may already
    // be connected, its Connected event gone before anyone listened.
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        mqtt.Connected += _ => { Trigger(); return Task.CompletedTask; };
        Trigger();
        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await _wake.WaitAsync(Every, stoppingToken);
                if (!mqtt.IsConnected) continue;   // the next (re)connect triggers a publish
                var stats = await outbox.StatsAsync(stoppingToken);
                var status = Online(options, stats, health.IsConnected, clock.GetUtcNow());
                if (!await mqtt.PublishStatusAsync(status, stoppingToken))
                    log.LogWarning("Gateway status not published (broker away)");
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    // Clean stop (service stopped, Ctrl+C): say "offline" ourselves before the MQTT client disconnects.
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        if (mqtt.IsConnected)
            await mqtt.PublishStatusAsync(Offline(options.Gateway.Id), cancellationToken);
    }

    public static GatewayStatusMessage Online(GatewayOptions o, OutboxStats stats, Func<string, bool> isConnected, DateTimeOffset now)
        => new(o.Gateway.Id, GatewayStates.Online, stats.QueueLength, stats.DroppedCount, stats.OldestAgeSec,
            o.Devices.ToDictionary(d => d.Machine, d => isConnected(d.Machine) ? GatewayStates.Connected : GatewayStates.Disconnected),
            now);

    // Also the LWT payload: no devices (the MES keeps the last list and marks them disconnected).
    public static GatewayStatusMessage Offline(string gatewayId) => new(gatewayId, GatewayStates.Offline);
}
