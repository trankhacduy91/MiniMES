using System.Collections.Concurrent;
using MiniMes.Contracts.Machines;
using MiniMes.EdgeGateway.Config;
using MiniMes.EdgeGateway.Pipeline;

namespace MiniMes.EdgeGateway.Health;

// Spec M4b 2.6 / 2.8: the gateway speaks for its machines' heartbeat. Every 5 s, one heartbeat per machine the gateway
// can read right now, carrying its last state (the MES brings a machine back from Offline with it, M4).
// A machine the gateway lost gets NO heartbeat (rule G7): after 30 s the MES watchdog shows it Offline. Never a fake
// "Down". Heartbeats go out QoS 0, not retained, and never through the outbox (step 5): an old heartbeat is worthless.
public sealed class HealthPublisher(GatewayOptions options)
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    private readonly Dictionary<string, string> _protocolOf = options.Devices.ToDictionary(d => d.Machine, d => d.Protocol);
    // Written by the pipeline, read by the gateway status publisher (another thread).
    private readonly ConcurrentDictionary<string, byte> _connected = new();
    private DateTime _lastAt = DateTime.MinValue;

    public bool IsConnected(string machine) => _connected.ContainsKey(machine);

    public void ConnectionChanged(string machine, bool connected)
    {
        if (connected) _connected[machine] = 0;
        else _connected.TryRemove(machine, out _);
    }

    // Called at every pipeline tick; returns the heartbeats that are due. A machine whose state is not known yet
    // (no state read since the connect) is skipped: the M4 heartbeat must carry a state.
    public IReadOnlyList<Outgoing> Due(DateTime nowUtc, Func<string, MachineState?> stateOf)
    {
        if (nowUtc - _lastAt < Interval) return [];
        _lastAt = nowUtc;
        var gw = options.Gateway;
        return _connected.Keys.Order()
            .Select(machine => (machine, state: stateOf(machine)))
            .Where(x => x.state is not null)
            .Select(x => new Outgoing(
                MachineTopic.Build(gw.Site, gw.Area, gw.Line, x.machine, MachineKinds.Heartbeat),
                new MachineMessage(MessageIdFactory.Create(x.machine, MachineKinds.Heartbeat, nowUtc, x.state!.Value.ToString()), x.machine,
                    new DateTimeOffset(DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc)), State: x.state, Source: _protocolOf[x.machine]),
                Retain: false))
            .ToList();
    }
}
