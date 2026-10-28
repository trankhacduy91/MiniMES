using System.Net.Sockets;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using MiniMes.Contracts.Machines;
using MiniMes.EdgeGateway.Config;
using NModbus;

namespace MiniMes.EdgeGateway.Drivers.Modbus;

// Spec M4b 2.5: Modbus TCP master. The slave never sends anything by itself: every PollMs the driver reads ONE block
// of holding registers (FC03) and cuts the tags out of it (one request instead of one per tag, spec 2.5 trap 1).
// Two kinds of failure, handled differently:
//   - the slave ANSWERS with an exception code (illegal address...): the link is fine, the values are Bad (rule G8);
//   - no answer (timeout, connection reset, slave switched off): the link is lost -> reconnect with backoff.
// Every poll yields every tag, changed or not; sending state/alarm only when they change is the normalizer's job (step 4).
public sealed class ModbusDriver(DeviceOptions device, ChannelWriter<DeviceEvent> events, TimeProvider clock,
    ILogger<ModbusDriver> log) : IDeviceDriver
{
    private string? _lastSlaveError;

    public string Machine => device.Machine;
    public string Protocol => Protocols.Modbus;

    public async Task RunAsync(CancellationToken ct)
    {
        var backoff = Backoff.Exponential(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30));
        while (!ct.IsCancellationRequested)
        {
            var connected = false;
            string reason;
            try
            {
                using var tcp = new TcpClient();
                using (var connect = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    connect.CancelAfter(device.TimeoutMs);   // a host that does not answer: give up, do not wait 20 s for the OS
                    try { await tcp.ConnectAsync(device.Host, device.Port, connect.Token); }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new TimeoutException($"connect timed out after {device.TimeoutMs} ms"); }
                }
                using var master = new ModbusFactory().CreateMaster(tcp);
                master.Transport.ReadTimeout = device.TimeoutMs;
                master.Transport.WriteTimeout = device.TimeoutMs;
                master.Transport.Retries = 0;   // the next poll is the retry; 3 retries would freeze the loop 4 x timeout

                connected = true;
                backoff.Reset();
                log.LogInformation("Modbus {Machine} connected to {Host}:{Port} unit {UnitId}, reading {Count} registers from {Start} every {PollMs} ms",
                    Machine, device.Host, device.Port, device.UnitId, device.Count, device.StartAddress, device.PollMs);
                events.TryWrite(new ConnectionChanged(Machine, Connected: true));

                using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(device.PollMs), clock);
                do await PollAsync(master, ct);
                while (await timer.WaitForNextTickAsync(ct));
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) when (ex is IOException or SocketException or TimeoutException or ObjectDisposedException or InvalidOperationException)
            {
                reason = ex.Message;
            }

            if (connected) events.TryWrite(new ConnectionChanged(Machine, Connected: false, reason));
            var delay = backoff.Next();
            log.LogWarning("Modbus {Machine} at {Host}:{Port}: {Reason}; retry in {Delay}s", Machine, device.Host, device.Port, reason, delay.TotalSeconds);
            try { await Task.Delay(delay, clock, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task PollAsync(IModbusMaster master, CancellationToken ct)
    {
        ushort[] block;
        try
        {
            // WaitAsync: a last guard in case the socket timeout is not honoured on the async path.
            block = await master.ReadHoldingRegistersAsync(device.UnitId, device.StartAddress, device.Count)
                .WaitAsync(TimeSpan.FromMilliseconds(device.TimeoutMs * 2), clock, ct);
        }
        catch (SlaveException ex)
        {
            // The PLC answered "no" (e.g. illegal data address after a firmware change): report Bad, keep polling.
            var error = $"slave exception {ex.SlaveExceptionCode}";
            if (error != _lastSlaveError) log.LogError("Modbus {Machine}: {Error} reading {Count} registers from {Start}", Machine, error, device.Count, device.StartAddress);
            _lastSlaveError = error;
            var at = clock.GetUtcNow().UtcDateTime;
            foreach (var tag in device.Tags)
                events.TryWrite(new RawValue(Machine, tag.Name, null, DataQuality.Bad, at, MachineSources.Modbus));
            return;
        }

        _lastSlaveError = null;
        // Rule G9: Modbus carries no timestamp, so the gateway's clock at the moment of the read.
        var now = clock.GetUtcNow().UtcDateTime;
        foreach (var tag in device.Tags)
            events.TryWrite(new RawValue(Machine, tag.Name, RegisterDecoder.Decode(block, tag.Offset, tag.Type, tag.WordOrder),
                DataQuality.Good, now, MachineSources.Modbus));
    }
}
