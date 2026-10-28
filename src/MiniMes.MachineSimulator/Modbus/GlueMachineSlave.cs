using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using MiniMes.MachineSimulator.Config;
using NModbus;

namespace MiniMes.MachineSimulator.Modbus;

// Spec M4b 2.4: the old glue dispenser's PLC, a Modbus TCP slave. Holding registers (0-based wire addresses; the
// machine's manual says 40001-40004):
//   0-1  cumulative counter, uint32 stored CDAB (low word first) on purpose: read as ABCD it shows 2 billion boards
//   2    state: 1 Running, 2 Idle, 3 Down, 4 Setup, 5 PlannedStop
//   3    alarm code, 0 = none (the digits of the machine's alarm code: G301 -> 301)
// "modbus stop" closes the listener and every master's connection (the PLC is switched off); "modbus start" builds a
// new network and writes the last registers again, so the gateway reads the same counter after the restart.
public sealed class GlueMachineSlave(string machine, ModbusOptions options, ILogger log)
{
    public const int RegisterCount = 4;

    private readonly Lock _gate = new();
    private readonly ushort[] _registers = new ushort[RegisterCount];
    private TcpListener? _listener;
    private IModbusSlaveNetwork? _network;
    private IModbusSlave? _slave;
    private CancellationTokenSource? _stop;

    // "G301" -> 301, "E999" -> 999; null when the code has no number that fits in a register.
    public static ushort? AlarmRegister(string code)
        => ushort.TryParse(new string(code.Where(char.IsAsciiDigit).ToArray()), out var n) && n > 0 ? n : null;

    public string Machine => machine;
    public string Endpoint => $"modbus-tcp://0.0.0.0:{options.Port} unit {options.UnitId}";
    public bool IsRunning { get { lock (_gate) return _network is not null; } }

    // Machine loop: every tick. One WritePoints for the 4 registers: NModbus locks the store around a write and a read,
    // so a master never reads the low word of the new counter with the high word of the old one.
    public void Publish(uint counter, ushort state, ushort alarm)
    {
        lock (_gate)
        {
            _registers[0] = (ushort)(counter & 0xFFFF);   // C D: low word first
            _registers[1] = (ushort)(counter >> 16);      // A B
            _registers[2] = state;
            _registers[3] = alarm;
            _slave?.DataStore.HoldingRegisters.WritePoints(0, _registers);
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_network is not null) return;
            try
            {
                var factory = new ModbusFactory();
                // Dual mode (IPv6 + IPv4): "localhost" resolves to ::1 first, and on Windows a refused loopback connect
                // takes about 2 s before the client falls back to 127.0.0.1, longer than the gateway's connect timeout.
                // 502 is the standard port; 5020 needs no admin rights.
                _listener = TcpListener.Create(options.Port);
                _listener.Start();
                _network = factory.CreateSlaveNetwork(_listener);
                _slave = factory.CreateSlave(options.UnitId);
                _network.AddSlave(_slave);
                _slave.DataStore.HoldingRegisters.WritePoints(0, _registers);
                _stop = new CancellationTokenSource();
                _ = ListenAsync(_network, _stop.Token);
                log.LogInformation("{Machine}: Modbus TCP slave on port {Port}, unit {UnitId}", machine, options.Port, options.UnitId);
            }
            catch (SocketException ex)
            {
                // Port taken: the machine keeps running without its Modbus side.
                log.LogError("{Machine}: Modbus slave did not start on port {Port}: {Error}", machine, options.Port, ex.Message);
                Close();
            }
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (_network is null) return;
            Close();
            log.LogInformation("{Machine}: Modbus slave stopped", machine);
        }
    }

    // Disposing the network also closes the masters' connections: the gateway's next read fails at once.
    private void Close()
    {
        _stop?.Cancel();
        _network?.Dispose();
        _listener?.Stop();
        (_network, _slave, _listener, _stop) = (null, null, null, null);
    }

    private async Task ListenAsync(IModbusSlaveNetwork network, CancellationToken ct)
    {
        try { await network.ListenAsync(ct); }
        catch (Exception ex) when (ct.IsCancellationRequested || ex is ObjectDisposedException) { }   // stopped
        catch (Exception ex) { log.LogError(ex, "{Machine}: Modbus slave failed", machine); }
    }
}
