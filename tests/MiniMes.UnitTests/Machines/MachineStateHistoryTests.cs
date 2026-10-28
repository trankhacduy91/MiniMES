using MiniMes.Domain.Common;
using MiniMes.Domain.Machines;

namespace MiniMes.UnitTests.Machines;

// Spec M5 step 6: the reason of a stop segment.
public class MachineStateHistoryTests
{
    private static readonly DateTime T0 = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Reason_and_trimmed_note_are_kept_and_can_be_corrected()
    {
        var down = new MachineStateHistory(1, MachineState.Down, T0);

        down.AssignReason(5, "  reel bent ");
        Assert.Equal((5, "reel bent"), (down.DowntimeReasonId, down.DowntimeNote));

        down.AssignReason(7, " ");
        Assert.Equal((7, (string?)null), (down.DowntimeReasonId, down.DowntimeNote));
    }

    [Fact]
    public void Running_segment_or_a_bad_reason_is_refused()
    {
        Assert.Throws<DomainException>(() => new MachineStateHistory(1, MachineState.Running, T0).AssignReason(5));
        Assert.Throws<DomainException>(() => new MachineStateHistory(1, MachineState.Down, T0).AssignReason(0));
        Assert.Throws<DomainException>(() => new MachineStateHistory(1, MachineState.Down, T0)
            .AssignReason(5, new string('x', MachineStateHistory.NoteMaxLength + 1)));
    }
}
