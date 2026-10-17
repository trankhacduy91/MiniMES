namespace MiniMes.Infrastructure.Machines;

// When the ingest connected (null = not connected). The watchdog's grace period reads it: a MES that cannot hear
// the broker concludes nothing about the machines (spec 2.5).
public sealed class MqttConnectionState
{
    private long _connectedSinceTicks;   // 0 = not connected; Interlocked: written by the MQTT loop, read by the watchdog

    public DateTime? ConnectedSince
    {
        get
        {
            var ticks = Interlocked.Read(ref _connectedSinceTicks);
            return ticks == 0 ? null : new DateTime(ticks, DateTimeKind.Utc);
        }
    }

    public void Connected(DateTime at) => Interlocked.Exchange(ref _connectedSinceTicks, at.Ticks);

    public void Disconnected() => Interlocked.Exchange(ref _connectedSinceTicks, 0);
}
