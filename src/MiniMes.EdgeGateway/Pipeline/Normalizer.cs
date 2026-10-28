using System.Globalization;
using Microsoft.Extensions.Logging;
using MiniMes.Contracts.Machines;
using MiniMes.EdgeGateway.Config;
using MiniMes.EdgeGateway.Drivers;

namespace MiniMes.EdgeGateway.Pipeline;

// One MQTT message for the M4 contract (rule G1): topic of the Unified Namespace, the JSON body, retained or not.
public sealed record Outgoing(string Topic, MachineMessage Message, bool Retain);

// Spec M4b 2.6: raw readings (any protocol) -> M4 messages. What goes out and when, per tag kind:
//   count    counter              on change, and at least every 30 s (the MES gets the total even if nothing moves)
//   state    state via map        only on change (retained on the broker; the heartbeat repeats it)
//   alarm    alarmCode (0 = null) only on change
//   process  tag + value          beyond the deadband, and at least every 60 s (rule G10)
// Quality (rule G8, as far as the M4 ingest allows it): a Bad process reading goes out with the last good value and
// quality Bad (the MES stores it, SPC ignores it; a process message without a value would be refused). A Bad count,
// state or alarm says nothing usable and the ingest would drop it: it is not sent.
// Not thread-safe on purpose: the pipeline's single reader is the only caller.
public sealed class Normalizer
{
    public static readonly TimeSpan CountRefresh = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan ProcessRefresh = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan WarnEvery = TimeSpan.FromMinutes(1);

    private readonly GatewayIdentity _gw;
    private readonly Dictionary<(string Machine, string Tag), TagOptions> _tags;
    private readonly Dictionary<(string Machine, string Tag), Published> _last = [];
    private readonly Dictionary<(string Machine, string Tag), DateTime> _warnedAt = [];
    private readonly ILogger _log;

    public Normalizer(GatewayOptions options, ILogger<Normalizer> log)
    {
        _gw = options.Gateway;
        _tags = options.Devices.SelectMany(d => d.Tags.Select(t => (d.Machine, t)))
            .ToDictionary(x => (x.Machine, x.t.Name), x => x.t);
        _log = log;
    }

    // What was last sent for a tag: the comparable value, when (GATEWAY clock: a PLC with a skewed clock must not make
    // the refresh fire at every tick or never), and what is needed to send it again.
    private sealed record Published(object? Value, DataQuality Quality, DateTime SentAt, string Source);

    // The machine's state as last sent: the heartbeat carries it (the MES brings a machine back from Offline with it).
    public MachineState? CurrentState(string machine)
        => _tags.Where(t => t.Key.Machine == machine && t.Value.Kind == TagKinds.State)
            .Select(t => _last.GetValueOrDefault(t.Key)?.Value as MachineState?)
            .FirstOrDefault();

    // nowUtc = the gateway's clock; raw.SourceTsUtc (the PLC's clock) only goes into the message.
    public IReadOnlyList<Outgoing> Accept(RawValue raw, DateTime nowUtc)
    {
        if (!_tags.TryGetValue((raw.Machine, raw.Tag), out var tag)) return [];
        var key = (raw.Machine, raw.Tag);
        var last = _last.GetValueOrDefault(key);

        return tag.Kind switch
        {
            TagKinds.Count => Count(key, raw, last, nowUtc),
            TagKinds.State => State(key, tag, raw, last, nowUtc),
            TagKinds.Alarm => Alarm(key, tag, raw, last, nowUtc),
            TagKinds.Process => Process(key, tag, raw, last, nowUtc),
            _ => [],
        };
    }

    // Called every few seconds: the "at least every 30/60 s" messages for tags that did not change (an OPC UA
    // subscription sends nothing while the value stays). Ts = now: the value is confirmed at this moment.
    public IReadOnlyList<Outgoing> Refresh(DateTime nowUtc)
    {
        var due = new List<Outgoing>();
        foreach (var (key, last) in _last.ToList())
        {
            var kind = _tags[key].Kind;
            var every = kind == TagKinds.Count ? CountRefresh : kind == TagKinds.Process ? ProcessRefresh : (TimeSpan?)null;
            if (every is null || nowUtc - last.SentAt < every) continue;
            var raw = new RawValue(key.Machine, key.Tag, last.Value, last.Quality, nowUtc, last.Source);
            due.AddRange(kind == TagKinds.Count ? Emit(key, raw, nowUtc, Count: (long)last.Value!) : Emit(key, raw, nowUtc, Process: (double)last.Value!));
        }
        return due;
    }

    // The machine is gone (rule G7): forget what was sent, so everything goes out again after the reconnect and no
    // refresh claims "still 8002" for a machine we cannot read.
    public void Disconnected(string machine)
    {
        foreach (var key in _last.Keys.Where(k => k.Machine == machine).ToList()) _last.Remove(key);
    }

    private IReadOnlyList<Outgoing> Count((string, string) key, RawValue raw, Published? last, DateTime now)
    {
        if (raw.Quality != DataQuality.Good) return [];
        if (!TryInteger(raw.Value, out var counter) || counter < 0)
        {
            Warn(key, now, "counter value {Value} is not a non-negative integer", raw.Value);
            return [];
        }
        if (last is not null && Equals(last.Value, counter) && now - last.SentAt < CountRefresh) return [];
        return Emit(key, raw, now, Count: counter);
    }

    private IReadOnlyList<Outgoing> State((string, string) key, TagOptions tag, RawValue raw, Published? last, DateTime now)
    {
        if (raw.Quality != DataQuality.Good) return [];
        if (!TryInteger(raw.Value, out var code) || !tag.Map.TryGetValue(code.ToString(CultureInfo.InvariantCulture), out var name)
            || !Enum.TryParse<MachineState>(name, out var state))
        {
            Warn(key, now, "state code {Value} is not in the map: dropped", raw.Value);
            return [];
        }
        if (last is not null && Equals(last.Value, state)) return [];
        return Emit(key, raw, now, State: state);
    }

    private IReadOnlyList<Outgoing> Alarm((string, string) key, TagOptions tag, RawValue raw, Published? last, DateTime now)
    {
        if (raw.Quality != DataQuality.Good) return [];
        string? code;
        if (TryInteger(raw.Value, out var n)) code = n == 0 && tag.ZeroMeansNone ? null : n.ToString(CultureInfo.InvariantCulture);
        else code = raw.Value is string s && s.Length > 0 ? s : null;
        if (last is not null && Equals(last.Value, code)) return [];
        // The first reading of "no alarm" is not news: the MES starts without one.
        if (last is null && code is null) { Remember(key, null, raw, now); return []; }
        return Emit(key, raw, now, Alarm: code);   // null = alarm cleared (M4 contract)
    }

    private IReadOnlyList<Outgoing> Process((string, string) key, TagOptions tag, RawValue raw, Published? last, DateTime now)
    {
        double value;
        if (raw.Quality == DataQuality.Bad || raw.Value is null)
        {
            // The sensor is broken: say so once, with the last good value; nothing at all before the first good one.
            if (last is null || last.Quality == DataQuality.Bad) return [];
            value = (double)last.Value!;
        }
        else if (!TryDouble(raw.Value, out value))
        {
            Warn(key, now, "process value {Value} is not a number", raw.Value);
            return [];
        }

        var quality = raw.Value is null ? DataQuality.Bad : raw.Quality;
        if (last is not null && last.Quality == quality
            && Math.Abs(value - (double)last.Value!) <= (tag.Deadband ?? 0)
            && now - last.SentAt < ProcessRefresh) return [];
        return Emit(key, raw with { Quality = quality }, now, Process: value);
    }

    private IReadOnlyList<Outgoing> Emit((string Machine, string Tag) key, RawValue raw, DateTime now,
        long? Count = null, MachineState? State = null, string? Alarm = null, double? Process = null)
    {
        var kind = _tags[key].Kind;
        object? value = Count ?? (object?)State ?? Alarm ?? (object?)Process;
        var valueText = value switch
        {
            null => "none",
            double d => d.ToString("R", CultureInfo.InvariantCulture) + "|" + raw.Quality,
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString()!,
        };
        var message = new MachineMessage(MessageIdFactory.Create(key.Machine, key.Tag, raw.SourceTsUtc, valueText), key.Machine,
            new DateTimeOffset(DateTime.SpecifyKind(raw.SourceTsUtc, DateTimeKind.Utc)),
            State: State, Counter: Count, AlarmCode: Alarm,
            Tag: kind == TagKinds.Process ? key.Tag : null, Value: Process,
            Quality: raw.Quality, Source: raw.Source);
        var topic = kind == TagKinds.Process
            ? MachineTopic.BuildProcess(_gw.Site, _gw.Area, _gw.Line, key.Machine, key.Tag)
            : MachineTopic.Build(_gw.Site, _gw.Area, _gw.Line, key.Machine, kind);

        Remember(key, value, raw, now);
        return [new Outgoing(topic, message, Retain: kind == TagKinds.State)];   // M4: only state is retained
    }

    private void Remember((string, string) key, object? value, RawValue raw, DateTime now)
        => _last[key] = new Published(value, raw.Quality, now, raw.Source);

    // A PLC sending a code nobody mapped would flood the log at every poll: once a minute per tag is enough.
    private void Warn((string Machine, string Tag) key, DateTime now, string message, object? value)
    {
        if (_warnedAt.TryGetValue(key, out var at) && now - at < WarnEvery) return;
        _warnedAt[key] = now;
        _log.LogWarning("{Machine}.{Tag}: " + message, key.Machine, key.Tag, value);
    }

    private static bool TryInteger(object? value, out long result)
    {
        switch (value)
        {
            case byte or sbyte or short or ushort or int or uint or long:
                result = Convert.ToInt64(value, CultureInfo.InvariantCulture);
                return true;
            case ulong u when u <= long.MaxValue:
                result = (long)u;
                return true;
            case double or float when Convert.ToDouble(value, CultureInfo.InvariantCulture) is var d && d == Math.Floor(d) && Math.Abs(d) < 1e15:
                result = (long)Convert.ToDouble(value, CultureInfo.InvariantCulture);
                return true;
            default:
                result = 0;
                return false;
        }
    }

    private static bool TryDouble(object? value, out double result)
    {
        switch (value)
        {
            // float -> double through its shortest text: 245.1f must stay 245.1, not 245.10000610351562.
            case float f:
                result = double.Parse(f.ToString("R", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
                return !double.IsNaN(result) && !double.IsInfinity(result);
            case double or byte or sbyte or short or ushort or int or uint or long or ulong or decimal:
                result = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                return !double.IsNaN(result) && !double.IsInfinity(result);
            default:
                result = 0;
                return false;
        }
    }
}
