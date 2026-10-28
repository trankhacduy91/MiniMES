namespace MiniMes.EdgeGateway.Drivers;

// Spec M4b 2.8: wait 1, 2, 4... up to 30 s between reconnect attempts, back to 1 s once connected.
// A server that is restarting is not hammered, and a short outage is still recovered within a few seconds.
// Jitter (step 6): each delay is moved by up to +-jitter (20 %) at random, so 20 gateways that lost the same server at
// the same second do not all knock again at the same second when it comes back.
public sealed class Backoff(TimeSpan first, TimeSpan max, double jitter = 0, Random? random = null)
{
    private readonly TimeSpan _first = first;
    private readonly Random _random = random ?? Random.Shared;
    private TimeSpan _next = first;

    public static Backoff Exponential(TimeSpan first, TimeSpan max, double jitter = 0.2) => new(first, max, jitter);

    public TimeSpan Next()
    {
        var delay = _next;
        _next = TimeSpan.FromTicks(Math.Min(_next.Ticks * 2, max.Ticks));
        if (jitter <= 0) return delay;
        var factor = 1 + jitter * (2 * _random.NextDouble() - 1);   // 0.8 .. 1.2
        return TimeSpan.FromTicks(Math.Min((long)(delay.Ticks * factor), max.Ticks));
    }

    public void Reset() => _next = _first;
}
