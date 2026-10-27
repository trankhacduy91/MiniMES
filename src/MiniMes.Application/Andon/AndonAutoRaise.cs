using MiniMes.Application.Machines;
using DomainState = MiniMes.Domain.Machines.MachineState;

namespace MiniMes.Application.Andon;

public sealed record DownMachine(int MachineId, DateTime DownSince);

// Spec M4 2.9: a machine Down without a break for at least X seconds gets a Machine Andon raised by "system", so
// nobody forgets to call for help. X must filter short stops (a 5-second jam is not worth a call: Andon fatigue).
// Only Down: Offline is a connection problem (spec 1.3 at 14:10), PlannedStop / Setup / Idle are not failures.
public static class AndonAutoRaise
{
    public static readonly TimeSpan DefaultAfter = TimeSpan.FromSeconds(60);

    public static IReadOnlyList<DownMachine> Due(IEnumerable<MachineRuntimeSnapshot> machines, DateTime nowUtc, TimeSpan after)
        => machines
            .Where(m => m.State == DomainState.Down && m.StateSince is { } since && nowUtc - since >= after)
            .Select(m => new DownMachine(m.MachineId, m.StateSince!.Value))
            .ToList();
}
