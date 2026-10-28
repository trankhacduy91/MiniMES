using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MiniMes.EdgeGateway.Drivers;
using MiniMes.EdgeGateway.Health;
using MiniMes.EdgeGateway.Mqtt;
using MQTTnet.Protocol;

namespace MiniMes.EdgeGateway.Pipeline;

// The clock's beat, written into the same channel as the drivers' events: the 30/60 s refreshes and the heartbeats
// are handled by the same single reader, so the normalizer never needs a lock.
public sealed record PipelineTick(DateTime NowUtc) : DeviceEvent(Machine: "");

// Spec M4b 2.1 / 2.6: the single reader of the channel. Raw readings -> Normalizer -> M4 messages -> MQTT;
// connection changes -> heartbeats on/off; ticks -> refreshes and heartbeats.
// Step 4 publishes straight away (lost while the broker is away); step 5 puts the SQLite outbox in between.
public sealed class PipelineService(
    ChannelReader<DeviceEvent> events,
    ChannelWriter<DeviceEvent> writer,
    Normalizer normalizer,
    HealthPublisher health,
    GatewayMqttClient mqtt,
    TimeProvider clock,
    ILogger<PipelineService> log) : BackgroundService
{
    private static readonly TimeSpan TickEvery = TimeSpan.FromSeconds(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var ticking = TickAsync(stoppingToken);
        try
        {
            await foreach (var e in events.ReadAllAsync(stoppingToken))
            {
                var now = clock.GetUtcNow().UtcDateTime;
                switch (e)
                {
                    case RawValue v:
                        await PublishAsync(normalizer.Accept(v, now), MqttQualityOfServiceLevel.AtLeastOnce, stoppingToken);
                        break;
                    case ConnectionChanged c:
                        health.ConnectionChanged(c.Machine, c.Connected);
                        if (!c.Connected) normalizer.Disconnected(c.Machine);
                        log.LogWarning("{Machine} {Connection}{Reason}", c.Machine, c.Connected ? "connected" : "DISCONNECTED",
                            c.Reason is null ? "" : $": {c.Reason}");
                        break;
                    case PipelineTick t:
                        await PublishAsync(normalizer.Refresh(t.NowUtc), MqttQualityOfServiceLevel.AtLeastOnce, stoppingToken);
                        // QoS 0: a lost heartbeat is replaced 5 s later.
                        await PublishAsync(health.Due(t.NowUtc, normalizer.CurrentState), MqttQualityOfServiceLevel.AtMostOnce, stoppingToken, quiet: true);
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        await ticking;
    }

    private async Task TickAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TickEvery, clock);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
                writer.TryWrite(new PipelineTick(clock.GetUtcNow().UtcDateTime));
        }
        catch (OperationCanceledException) { }
    }

    private async Task PublishAsync(IReadOnlyList<Outgoing> messages, MqttQualityOfServiceLevel qos, CancellationToken ct, bool quiet = false)
    {
        foreach (var m in messages)
        {
            var sent = await mqtt.PublishAsync(m.Topic, m.Message, m.Retain, qos, ct);
            var what = m.Message.State?.ToString() ?? m.Message.Counter?.ToString() ?? m.Message.Value?.ToString()
                       ?? (m.Topic.EndsWith("/alarm", StringComparison.Ordinal) ? m.Message.AlarmCode ?? "(cleared)" : "");
            if (!sent)
                log.LogWarning("NOT SENT (broker away, lost until the outbox of step 5) {Topic} {What}", m.Topic, what);
            else if (!quiet)
                log.LogInformation("-> {Topic} {What} {Quality}{Retain}", m.Topic, what, m.Message.Quality, m.Retain ? " retained" : "");
        }
    }
}
