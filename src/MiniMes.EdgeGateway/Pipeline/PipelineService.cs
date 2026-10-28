using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MiniMes.EdgeGateway.Drivers;

namespace MiniMes.EdgeGateway.Pipeline;

// Spec M4b 2.1: the single reader of the drivers' channel, so events are handled one at a time and in order.
// Step 2 only logs each event; step 4 turns them into M4 messages (Normalizer) and step 5 queues them (outbox).
public sealed class PipelineService(ChannelReader<DeviceEvent> events, ILogger<PipelineService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var e in events.ReadAllAsync(stoppingToken))
            {
                switch (e)
                {
                    case RawValue v:
                        log.LogInformation("{Machine} {Tag} = {Value} {Quality} @ {SourceTs:HH:mm:ss.fff}Z ({Source})",
                            v.Machine, v.Tag, v.Value, v.Quality, v.SourceTsUtc, v.Source);
                        break;
                    case ConnectionChanged c:
                        log.LogWarning("{Machine} {Connection}{Reason}", c.Machine, c.Connected ? "connected" : "DISCONNECTED",
                            c.Reason is null ? "" : $": {c.Reason}");
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
