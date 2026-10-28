using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MiniMes.EdgeGateway.Mqtt;
using MQTTnet.Protocol;

namespace MiniMes.EdgeGateway.Queue;

// Spec M4b 2.7: the only way out of the outbox. ONE loop, ONE message at a time, in Id order (rule G5): publish QoS 1,
// wait for the PUBACK, delete, next. A failed publish stops the batch and the same message is tried again: order
// before speed (sending in parallel would mix the order and could make the MES see a counter reset that is not one).
// A crash between PUBACK and delete resends one message after the restart: same payload, same msgId, the MES drops it.
public sealed class ForwarderService(SqliteOutbox outbox, GatewayMqttClient mqtt, TimeProvider clock, ILogger<ForwarderService> log)
    : BackgroundService
{
    private const int BatchSize = 200;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        DateTimeOffset? outageSince = null, backSince = null;   // the broker was away: report the catch-up once done
        var sentInDrain = 0;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                if (!mqtt.IsConnected)                    // the client reconnects by itself; the queue just waits
                {
                    outageSince ??= clock.GetUtcNow();
                    backSince = null;
                    await Task.Delay(TimeSpan.FromSeconds(1), clock, stoppingToken);
                    continue;
                }
                if (outageSince is not null) backSince ??= clock.GetUtcNow();
                var batch = await outbox.PeekAsync(BatchSize, stoppingToken);
                if (batch.Count == 0)
                {
                    if (outageSince is { } down && backSince is { } back)
                        log.LogInformation("Outbox caught up: {Count} messages forwarded in {Seconds:F1} s after the broker came back (outage {Outage:F0} s)",
                            sentInDrain, (clock.GetUtcNow() - back).TotalSeconds, (back - down).TotalSeconds);
                    (outageSince, backSince, sentInDrain) = (null, null, 0);
                    await outbox.WaitForNewAsync(TimeSpan.FromSeconds(1), stoppingToken);
                    continue;
                }

                foreach (var m in batch)
                {
                    if (!await mqtt.PublishRawAsync(m.Topic, m.Payload, m.Retain, MqttQualityOfServiceLevel.AtLeastOnce, stoppingToken))
                        break;                            // keep the order: retry this very message in the next round
                    await outbox.DeleteAsync(m.Id, stoppingToken);   // only after the PUBACK (rule G4)
                    sentInDrain++;
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
