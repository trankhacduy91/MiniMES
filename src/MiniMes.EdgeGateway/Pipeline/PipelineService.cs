using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MiniMes.EdgeGateway.Drivers;
using MiniMes.EdgeGateway.Health;
using MiniMes.EdgeGateway.Mqtt;
using MiniMes.EdgeGateway.Queue;
using MQTTnet.Protocol;

namespace MiniMes.EdgeGateway.Pipeline;

// The clock's beat, written into the same channel as the drivers' events: the 30/60 s refreshes and the heartbeats
// are handled by the same single reader, so the normalizer never needs a lock.
public sealed record PipelineTick(DateTime NowUtc) : DeviceEvent(Machine: "");

// Spec M4b 2.1 / 2.6 / 2.7: the single reader of the channel. Raw readings -> Normalizer -> M4 messages -> OUTBOX
// (the ForwarderService sends them); connection changes -> heartbeats on/off; ticks -> refreshes and heartbeats.
// Every event waiting in the channel is handled before one write to the outbox: one SQLite transaction per batch
// instead of one per message (spec 2.7, trap 3), with no timer to tune.
public sealed class PipelineService(
    ChannelReader<DeviceEvent> events,
    ChannelWriter<DeviceEvent> writer,
    Normalizer normalizer,
    HealthPublisher health,
    SqliteOutbox outbox,
    GatewayMqttClient mqtt,
    TimeProvider clock,
    ILogger<PipelineService> log) : BackgroundService
{
    private static readonly TimeSpan TickEvery = TimeSpan.FromSeconds(1);
    private const int MaxBatch = 500;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var ticking = TickAsync(stoppingToken);
        try
        {
            while (await events.WaitToReadAsync(stoppingToken))
            {
                var batch = new List<Outgoing>();
                for (var n = 0; n < MaxBatch && events.TryRead(out var e); n++)
                    await HandleAsync(e, batch, stoppingToken);
                await QueueAsync(batch, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        await ticking;
    }

    private async Task HandleAsync(DeviceEvent e, List<Outgoing> batch, CancellationToken ct)
    {
        switch (e)
        {
            case RawValue v:
                batch.AddRange(normalizer.Accept(v, clock.GetUtcNow().UtcDateTime));
                break;
            case ConnectionChanged c:
                health.ConnectionChanged(c.Machine, c.Connected);
                if (!c.Connected) normalizer.Disconnected(c.Machine);
                log.LogWarning("{Machine} {Connection}{Reason}", c.Machine, c.Connected ? "connected" : "DISCONNECTED",
                    c.Reason is null ? "" : $": {c.Reason}");
                break;
            case PipelineTick t:
                batch.AddRange(normalizer.Refresh(t.NowUtc));
                // Heartbeats skip the outbox: QoS 0, and an old heartbeat replayed later would lie about the machine.
                foreach (var hb in health.Due(t.NowUtc, normalizer.CurrentState))
                    await mqtt.PublishAsync(hb.Topic, hb.Message, retain: false, MqttQualityOfServiceLevel.AtMostOnce, ct);
                break;
        }
    }

    private async Task QueueAsync(List<Outgoing> batch, CancellationToken ct)
    {
        if (batch.Count == 0) return;
        // Serialised once, here: the forwarder sends this exact text, so a resend carries the same msgId.
        await outbox.EnqueueAsync(batch.Select(m => (m.Topic, GatewayMqttClient.Serialize(m.Message), m.Retain)).ToList(), ct);
        foreach (var m in batch)
            log.LogInformation("queued {Topic} {What} {Quality}{Retain}", m.Topic, Describe(m), m.Message.Quality, m.Retain ? " retained" : "");
    }

    private static string Describe(Outgoing m)
        => m.Message.State?.ToString() ?? m.Message.Counter?.ToString() ?? m.Message.Value?.ToString()
           ?? (m.Topic.EndsWith("/alarm", StringComparison.Ordinal) ? m.Message.AlarmCode ?? "(cleared)" : "");

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
}
