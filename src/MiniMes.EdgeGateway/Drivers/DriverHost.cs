using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MiniMes.EdgeGateway.Drivers;

// Runs every driver side by side. A driver never throws (it reconnects by itself); should one have a bug and stop,
// the others keep reading their machines.
public sealed class DriverHost(IEnumerable<IDeviceDriver> drivers, ILogger<DriverHost> log) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var all = drivers.ToList();
        log.LogInformation("Edge gateway reading {Count} machines: {Machines}", all.Count,
            string.Join(", ", all.Select(d => $"{d.Machine} ({d.Protocol})")));
        return Task.WhenAll(all.Select(d => RunAsync(d, stoppingToken)));
    }

    private async Task RunAsync(IDeviceDriver driver, CancellationToken ct)
    {
        try { await driver.RunAsync(ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) { log.LogCritical(ex, "{Machine} ({Protocol}) driver stopped", driver.Machine, driver.Protocol); }
    }
}
