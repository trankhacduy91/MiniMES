using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using MiniMes.Contracts.Machines;
using MiniMes.MachineSimulator.Config;
using MiniMes.MachineSimulator.Mqtt;
using MQTTnet.Protocol;

namespace MiniMes.MachineSimulator.Machines;

// One machine, like a PLC: a cycle loop (small state machine) and a separate heartbeat task (spec 2.8).
// The counter is cumulative and keeps counting while nothing can be sent; the next count message carries the
// new total, so the MES loses no production when the broker or the network is away (spec 2.3).
public sealed class SimulatedMachine
{
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan CountMinInterval = TimeSpan.FromSeconds(1);   // at most 1 count msg/s
    private static readonly TimeSpan CountMaxInterval = TimeSpan.FromSeconds(30);  // at least every 30 s

    private readonly MachineOptions _cfg;
    private readonly string _site, _area;
    private readonly TimeSpan _cycle, _heartbeat;
    private readonly Random _rng;
    private readonly TimeProvider _clock;
    private readonly ILogger _log;
    private readonly Channel<MachineCommand> _commands = Channel.CreateUnbounded<MachineCommand>(new() { SingleReader = true });

    // Read by the heartbeat task and the reconnect callback, written only by the cycle loop.
    private volatile MachineState _state = MachineState.Running;
    private volatile bool _muted;
    private volatile int _skewSec;
    private volatile MachineSnapshot _snapshot;

    // Owned by the cycle loop.
    private long _counter, _good, _ng;
    private int _pendingGood, _pendingNg;            // the machine's own split since the last count sent
    private bool _countChanged;
    private DateTimeOffset _lastCountAt = DateTimeOffset.MinValue;
    private DateTimeOffset _nextCycleAt;
    private DateTimeOffset? _downUntil;              // null while Down = until "up"
    private int _forceNg;
    private bool _duplicateNext;

    public SimulatedMachine(SimulatorOptions sim, MachineOptions cfg, Random rng, MachinePublisher publisher, TimeProvider clock, ILogger log)
    {
        _cfg = cfg;
        (_site, _area) = (sim.Site, sim.Area);
        _cycle = TimeSpan.FromSeconds(cfg.CycleTimeSec);
        _heartbeat = TimeSpan.FromSeconds(sim.Mqtt.HeartbeatSec);
        _rng = rng;
        _clock = clock;
        _log = log;
        _counter = cfg.InitialCounter;
        _skewSec = cfg.ClockSkewSec;
        Publisher = publisher;
        Publisher.Connected = OnConnectedAsync;
        _snapshot = TakeSnapshot();
    }

    public string Code => _cfg.Code;
    public MachinePublisher Publisher { get; }
    public MachineSnapshot Snapshot => _snapshot;

    public bool Post(MachineCommand command) => _commands.Writer.TryWrite(command);

    public async Task RunAsync(CancellationToken ct)
    {
        _nextCycleAt = _clock.GetUtcNow() + _cycle;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                while (_commands.Reader.TryRead(out var command)) await ApplyAsync(command, ct);

                var now = _clock.GetUtcNow();
                if (_state == MachineState.Running && now >= _nextCycleAt) await CompleteCycleAsync(now, ct);
                else if (_state == MachineState.Down && _downUntil is { } until && now >= until) await ChangeStateAsync(MachineState.Running, ct);

                await FlushCountAsync(force: false, ct);
                _snapshot = TakeSnapshot();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogError(ex, "{Machine} cycle failed", Code);   // a bug must not silently stop one machine
            }
            await Task.Delay(Tick, _clock, ct);
        }
    }

    // Independent of the cycle, like the PLC's own communication task. QoS 0: a lost heartbeat is replaced 5 s later.
    // It carries the state, so the MES can bring a machine back from Offline at once (spec 2.5).
    public async Task HeartbeatAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_heartbeat, _clock);
        while (await timer.WaitForNextTickAsync(ct))
            if (!_muted)
                await Publisher.PublishAsync(Topic(MachineKinds.Heartbeat), NewMessage() with { State = _state },
                    MqttQualityOfServiceLevel.AtMostOnce, retain: false, ct);
    }

    private async Task CompleteCycleAsync(DateTimeOffset now, CancellationToken ct)
    {
        _nextCycleAt += _cycle;
        if (_nextCycleAt < now) _nextCycleAt = now + _cycle;   // was paused (Down, Setup): restart the rhythm

        if (_rng.NextDouble() < _cfg.DownProbPerCycle)
        {
            var seconds = _rng.Next(_cfg.DownDurationSec[0], _cfg.DownDurationSec[1] + 1);
            await GoDownAsync(seconds, _cfg.AlarmCodes[_rng.Next(_cfg.AlarmCodes.Length)], ct);
            return;
        }

        bool ng;
        if (_forceNg > 0) { ng = true; _forceNg--; } else ng = _rng.NextDouble() < _cfg.NgProb;
        _counter++;                                           // cumulative: never goes down, except "reset"
        if (ng) { _ng++; _pendingNg++; } else { _good++; _pendingGood++; }
        _countChanged = true;
    }

    private async Task ApplyAsync(MachineCommand command, CancellationToken ct)
    {
        switch (command)
        {
            case GoDown d:
                await GoDownAsync(d.Seconds, "E999", ct);       // E999: forced from the console
                break;
            case ChangeState c:
                await ChangeStateAsync(c.State, ct);
                break;
            case ForceNg n:
                _forceNg = n.Count;
                break;
            case ResetCounter:
                _counter = 0;                                  // the operator pressed reset on the HMI
                _countChanged = true;
                await FlushCountAsync(force: true, ct);
                break;
            case Mute { On: true }:
                _muted = true;                                 // silent, but the TCP connection stays: no LWT, the watchdog must find it
                break;
            case Mute { On: false }:
                _muted = false;
                await SendStateAsync(ct);
                await FlushCountAsync(force: true, ct);
                break;
            case DuplicateNext:
                _duplicateNext = true;
                break;
            case SetSkew s:
                _skewSec = s.Seconds;
                break;
        }
        _log.LogInformation("{Machine} {Command}", Code, command);
    }

    private async Task GoDownAsync(int? seconds, string alarmCode, CancellationToken ct)
    {
        _downUntil = seconds is null ? null : _clock.GetUtcNow().AddSeconds(seconds.Value);
        if (_state == MachineState.Down) return;               // already down: only the end changes
        await ChangeStateAsync(MachineState.Down, ct);
        await SendAsync(MachineKinds.Alarm, NewMessage() with { AlarmCode = alarmCode }, ct);
    }

    private async Task ChangeStateAsync(MachineState state, CancellationToken ct)
    {
        if (state == _state) return;
        if (_state == MachineState.Down)
        {
            _downUntil = null;
            await SendAsync(MachineKinds.Alarm, NewMessage() with { AlarmCode = null }, ct);   // null = alarm cleared
        }
        _log.LogInformation("{Machine} {From} -> {To}", Code, _state, state);
        _state = state;
        if (state == MachineState.Running) _nextCycleAt = _clock.GetUtcNow() + _cycle;
        await SendStateAsync(ct);
    }

    // Retained: whoever subscribes later (the ingest after a restart) gets the current state at once.
    private Task<bool> SendStateAsync(CancellationToken ct)
        => SendAsync(MachineKinds.State, NewMessage() with { State = _state }, ct, retain: true);

    // At most once a second when something changed, at least every 30 s even if nothing did (spec 2.3).
    // A count that could not be sent stays pending: deltas are kept and sent with the next one.
    private async Task FlushCountAsync(bool force, CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        var since = now - _lastCountAt;
        if (!force && !(_countChanged && since >= CountMinInterval) && since < CountMaxInterval) return;

        var message = NewMessage() with { Counter = _counter, GoodDelta = _pendingGood, NgDelta = _pendingNg };
        if (!await SendAsync(MachineKinds.Count, message, ct)) return;
        (_pendingGood, _pendingNg, _countChanged, _lastCountAt) = (0, 0, false, now);
    }

    private async Task<bool> SendAsync(string kind, MachineMessage message, CancellationToken ct, bool retain = false)
    {
        if (_muted) return false;
        var topic = Topic(kind);
        var sent = await Publisher.PublishAsync(topic, message, MqttQualityOfServiceLevel.AtLeastOnce, retain, ct);
        if (sent && _duplicateNext)
        {
            // What a QoS 1 redelivery looks like: the same msgId twice. The ingest must store and count it once.
            _duplicateNext = false;
            await Publisher.PublishAsync(topic, message, MqttQualityOfServiceLevel.AtLeastOnce, retain, ct);
            _log.LogInformation("{Machine} sent {Kind} {MsgId} twice", Code, kind, message.MsgId);
        }
        return sent;
    }

    // After every (re)connect: "online" and the current state (spec 2.3: state is sent right after connecting).
    private async Task OnConnectedAsync(CancellationToken ct)
    {
        if (_muted) return;
        await Publisher.PublishStatusAsync("online", ct);
        await Publisher.PublishAsync(Topic(MachineKinds.State), NewMessage() with { State = _state },
            MqttQualityOfServiceLevel.AtLeastOnce, retain: true, ct);
    }

    // Ts is the machine's clock: a skewed PLC sends a skewed time, the MES has to cope (spec 2.5).
    private MachineMessage NewMessage()
        => new(Guid.NewGuid(), Code, _clock.GetUtcNow().AddSeconds(_skewSec));

    private string Topic(string kind) => MachineTopic.Build(_site, _area, _cfg.Line, Code, kind);

    private MachineSnapshot TakeSnapshot()
        => new(Code, _state, _counter, _good, _ng, Publisher.IsConnected, _muted, _skewSec, _state == MachineState.Down ? _downUntil : null);
}
