namespace MiniMes.EdgeGateway.Drivers;

// Spec M4b 2.8: wait 1, 2, 4... up to 30 s between reconnect attempts, back to 1 s once connected.
// A server that is restarting is not hammered, and a short outage is still recovered within a few seconds.
public sealed class Backoff(TimeSpan first, TimeSpan max)
{
    private readonly TimeSpan _first = first;
    private TimeSpan _next = first;

    public static Backoff Exponential(TimeSpan first, TimeSpan max) => new(first, max);

    public TimeSpan Next()
    {
        var delay = _next;
        _next = TimeSpan.FromTicks(Math.Min(_next.Ticks * 2, max.Ticks));
        return delay;
    }

    public void Reset() => _next = _first;
}
