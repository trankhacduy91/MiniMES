using DomainState = MiniMes.Domain.Machines.MachineState;

namespace MiniMes.Application.Machines;

// Spec M4 2.5: Offline is never sent by a machine, the server infers it: nothing received for 30 s.
// Grace: when the MES itself is blind (ingest not connected, or connected for less than the timeout) it concludes
// nothing about the machines; otherwise every broker restart would turn every machine grey.
// Silence is measured from the later of "last message" and "ingest connected": after a broker restart a machine gets
// the full 30 s to reconnect and send its heartbeat before it counts as Offline.
// A reported "offline" (LWT) is confirmed after 3 s if the ingest kept the SAME connection since before the report and
// the machine said nothing since. A broker that shuts down publishes the LWT of every client and then drops the ingest
// too; that report must not count (found in step 5: two machines went Offline when only Mosquitto was stopped).
public sealed class OfflineDetector(TimeProvider clock, TimeSpan? timeout = null)
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan ReportConfirmation = TimeSpan.FromSeconds(3);

    public TimeSpan Timeout { get; } = timeout ?? DefaultTimeout;

    public IReadOnlyList<OfflineVerdict> Find(IEnumerable<MachineRuntimeSnapshot> machines, DateTime? ingestConnectedSince)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        if (ingestConnectedSince is not { } since) return [];          // blind: no verdict at all

        var verdicts = new List<OfflineVerdict>();
        foreach (var m in machines.Where(m => m.State != DomainState.Offline))
        {
            var lastSeen = m.LastSeenAt ?? DateTime.MinValue;
            if (m.OfflineReportedAt is { } reported && since <= reported && lastSeen <= reported && now - reported >= ReportConfirmation)
                verdicts.Add(new(m.MachineId, "status offline (LWT)"));
            else if (now - since >= Timeout && now - Max(lastSeen, since) > Timeout)   // never seen since start = since
                verdicts.Add(new(m.MachineId, $"no message for {Timeout.TotalSeconds:0} s"));
        }
        return verdicts;
    }

    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;
}

public sealed record OfflineVerdict(int MachineId, string Reason);
