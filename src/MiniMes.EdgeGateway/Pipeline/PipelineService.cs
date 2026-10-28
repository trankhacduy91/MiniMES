using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MiniMes.Contracts.Machines;
using MiniMes.EdgeGateway.Drivers;

namespace MiniMes.EdgeGateway.Pipeline;

// Spec M4b 2.1: the single reader of the drivers' channel, so events are handled one at a time and in order.
// Steps 2-3 only log the events; step 4 turns them into M4 messages (Normalizer) and step 5 queues them (outbox).
// A Modbus driver yields every tag at every poll: only changes are logged, or the log is one line per tag per second.
public sealed class PipelineService(ChannelReader<DeviceEvent> events, ILogger<PipelineService> log) : BackgroundService
{
    private readonly Dictionary<(string Machine, string Tag), (object? Value, DataQuality Quality)> _logged = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var e in events.ReadAllAsync(stoppingToken))
            {
                switch (e)
                {
                    case RawValue v when _logged.TryGetValue((v.Machine, v.Tag), out var last) && Equals(last.Value, v.Value) && last.Quality == v.Quality:
                        break;
                    case RawValue v:
                        _logged[(v.Machine, v.Tag)] = (v.Value, v.Quality);
                        log.LogInformation("{Machine} {Tag} = {Value} {Quality} @ {SourceTs:HH:mm:ss.fff}Z ({Source})",
                            v.Machine, v.Tag, v.Value, v.Quality, v.SourceTsUtc, v.Source);
                        break;
                    case ConnectionChanged c:
                        foreach (var key in _logged.Keys.Where(k => k.Machine == c.Machine).ToList()) _logged.Remove(key);   // log afresh after a reconnect
                        log.LogWarning("{Machine} {Connection}{Reason}", c.Machine, c.Connected ? "connected" : "DISCONNECTED",
                            c.Reason is null ? "" : $": {c.Reason}");
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
