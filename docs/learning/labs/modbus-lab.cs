// Spec M4b step 0: the Modbus Poll / mbpoll exercise as code, slave and master in one process.
// The slave is the glue machine of the spec (2.4): counter 100,000 stored CDAB in holding registers 0-1 (documented
// as 40001-40002), state in 2, alarm in 3, plus a float 123.456 stored ABCD in 10-11. The master then makes the
// classic mistakes on purpose: every word order, an address off by one, input registers (FC04) instead of holding (FC03).
//   dotnet run docs/learning/labs/modbus-lab.cs -- [port]
#:package NModbus@3.0.83
#:property PublishAot=false
using System.Net;
using System.Net.Sockets;
using NModbus;

var port = int.Parse(args.ElementAtOrDefault(0) ?? "5020");   // 502 is the standard port; 5020 needs no admin rights
const byte UnitId = 1;

// ---------- Slave (the machine): a register table in RAM, answered over TCP ----------
var factory = new ModbusFactory();
var listener = new TcpListener(IPAddress.Loopback, port);
listener.Start();
var network = factory.CreateSlaveNetwork(listener);
var slave = factory.CreateSlave(UnitId);
network.AddSlave(slave);
using var stop = new CancellationTokenSource();
var listening = network.ListenAsync(stop.Token);

const uint Counter = 100_000;   // 0x0001_86A0
var (hi, lo) = ((ushort)(Counter >> 16), (ushort)(Counter & 0xFFFF));
slave.DataStore.HoldingRegisters.WritePoints(0, [lo, hi, 1, 0]);   // CDAB: low word first; state 1 = Running; no alarm
var floatBits = (uint)BitConverter.SingleToInt32Bits(123.456f);
slave.DataStore.HoldingRegisters.WritePoints(10, [(ushort)(floatBits >> 16), (ushort)floatBits]);   // ABCD
Console.WriteLine($"Slave unit {UnitId} on port {port}: HR0..3 = {Hex(slave.DataStore.HoldingRegisters.ReadPoints(0, 4))}, HR10..11 = {Hex(slave.DataStore.HoldingRegisters.ReadPoints(10, 2))}");

// ---------- Master (the gateway): asks, the slave answers; nothing is ever pushed ----------
using var tcp = new TcpClient();
await tcp.ConnectAsync(IPAddress.Loopback, port);
using var master = factory.CreateMaster(tcp);
master.Transport.ReadTimeout = 2000;   // a dead slave must not hang the poll loop forever

// One request (FC03) for the whole block: 4 registers, wire address 0 = "40001" in the machine's manual.
var regs = await master.ReadHoldingRegistersAsync(UnitId, startAddress: 0, numberOfPoints: 4);
Console.WriteLine($"\nFC03 read 0..3 -> {Hex(regs)}   state={regs[2]} alarm={regs[3]}");
Console.WriteLine("Counter (HR0-1) decoded with each word order:");
foreach (var order in Enum.GetValues<WordOrder>())
    Console.WriteLine($"  {order}  {Decode(regs[0], regs[1], order),15:N0}{(order == WordOrder.CDAB ? "   <- the machine's real order" : "")}");

var f = await master.ReadHoldingRegistersAsync(UnitId, 10, 2);
Console.WriteLine($"\nFloat (HR10-11 = {Hex(f)}): ABCD {BitConverter.Int32BitsToSingle((int)Decode(f[0], f[1], WordOrder.ABCD))}, " +
                  $"CDAB {BitConverter.Int32BitsToSingle((int)Decode(f[0], f[1], WordOrder.CDAB))}");

// Off by one: the manual says 40001, someone types 1 instead of 0.
var shifted = await master.ReadHoldingRegistersAsync(UnitId, 1, 2);
Console.WriteLine($"\nOff by one (read 1..2 instead of 0..1) -> {Hex(shifted)} -> CDAB {Decode(shifted[0], shifted[1], WordOrder.CDAB):N0}");

// Wrong area: FC04 reads the input registers, a different table that is all zeros here.
var input = await master.ReadInputRegistersAsync(UnitId, 0, 4);
Console.WriteLine($"FC04 instead of FC03 -> {Hex(input)}");

stop.Cancel();
listener.Stop();
try { await listening; } catch (Exception) { /* listener closed */ }

static uint Decode(ushort r0, ushort r1, WordOrder order)
{
    static ushort Swap(ushort v) => (ushort)((v << 8) | (v >> 8));
    return order switch
    {
        WordOrder.ABCD => ((uint)r0 << 16) | r1,
        WordOrder.CDAB => ((uint)r1 << 16) | r0,
        WordOrder.BADC => ((uint)Swap(r0) << 16) | Swap(r1),
        WordOrder.DCBA => ((uint)Swap(r1) << 16) | Swap(r0),
        _ => throw new ArgumentOutOfRangeException(nameof(order)),
    };
}

static string Hex(IEnumerable<ushort> values) => string.Join(" ", values.Select(v => $"0x{v:X4}"));

enum WordOrder { ABCD, CDAB, BADC, DCBA }
