using System.Collections.Concurrent;

namespace MiniMes.Application.Machines;

// First layer against duplicates (spec M4 2.4): QoS 1 is "at least once", the broker may deliver a message again
// after a reconnect. Remembering the msgIds of the last minutes catches nearly every redelivery in RAM; the unique
// index on MsgId (step 1) catches the rest, e.g. right after the ingest restarted with an empty cache.
public sealed class RecentIdCache(TimeProvider clock, TimeSpan? ttl = null)
{
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(10);

    private readonly TimeSpan _ttl = ttl ?? DefaultTtl;
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _seen = new();
    private DateTimeOffset _lastPurge = clock.GetUtcNow();

    public int Count => _seen.Count;

    // true = first time seen (process it), false = duplicate within the TTL (drop it).
    public bool TryAdd(Guid msgId)
    {
        var now = clock.GetUtcNow();
        PurgeIfDue(now);
        if (_seen.TryAdd(msgId, now)) return true;
        // Seen, but so long ago that it is a new message reusing an id: count it once more.
        return _seen.TryGetValue(msgId, out var at) && now - at >= _ttl && _seen.TryUpdate(msgId, now, at);
    }

    // Once per TTL: memory stays at "messages of the last 10-20 minutes", whatever the uptime.
    private void PurgeIfDue(DateTimeOffset now)
    {
        if (now - _lastPurge < _ttl) return;
        _lastPurge = now;
        foreach (var (id, at) in _seen)
            if (now - at >= _ttl) _seen.TryRemove(new KeyValuePair<Guid, DateTimeOffset>(id, at));
    }
}
