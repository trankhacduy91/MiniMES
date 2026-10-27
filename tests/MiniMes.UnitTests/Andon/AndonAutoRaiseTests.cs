using MiniMes.Application.Andon;
using MiniMes.Application.Machines;
using MiniMes.Domain.Machines;

namespace MiniMes.UnitTests.Andon;

// Spec M4 2.9: which machines the watcher raises an automatic Andon for.
public class AndonAutoRaiseTests
{
    private static readonly DateTime Now = new(2026, 10, 27, 2, 43, 0, DateTimeKind.Utc);
    private static readonly TimeSpan X = TimeSpan.FromSeconds(60);

    [Theory]
    [InlineData(MachineState.Down, 60, true)]          // exactly X: due
    [InlineData(MachineState.Down, 59, false)]         // short stop: not worth a call (Andon fatigue)
    [InlineData(MachineState.Down, 3600, true)]
    [InlineData(MachineState.Offline, 3600, false)]    // connection problem, not a broken machine
    [InlineData(MachineState.PlannedStop, 3600, false)]
    [InlineData(MachineState.Setup, 3600, false)]
    [InlineData(MachineState.Idle, 3600, false)]
    [InlineData(MachineState.Running, 3600, false)]
    public void Only_machines_down_for_at_least_x_are_due(MachineState state, int secondsInState, bool due)
    {
        var since = Now.AddSeconds(-secondsInState);

        var result = AndonAutoRaise.Due([Snapshot(1, state, since)], Now, X);

        Assert.Equal(due, result.Count == 1);
        if (due) Assert.Equal(new DownMachine(1, since), result[0]);
    }

    [Fact]
    public void Machine_never_heard_from_is_not_due()
    {
        Assert.Empty(AndonAutoRaise.Due([Snapshot(1, null, null)], Now, X));
    }

    [Fact]
    public void Each_machine_is_judged_on_its_own()
    {
        var machines = new[]
        {
            Snapshot(1, MachineState.Down, Now.AddSeconds(-90)),
            Snapshot(2, MachineState.Down, Now.AddSeconds(-10)),
            Snapshot(3, MachineState.Running, Now.AddSeconds(-90)),
            Snapshot(4, MachineState.Down, Now.AddMinutes(-5)),
        };

        Assert.Equal([1, 4], AndonAutoRaise.Due(machines, Now, X).Select(d => d.MachineId));
    }

    private static MachineRuntimeSnapshot Snapshot(int id, MachineState? state, DateTime? since)
        => new(id, state, since, LastCounter: null, LastSeenAt: since, Process: []);
}
