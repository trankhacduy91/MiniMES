using MiniMes.Domain.Common;

namespace MiniMes.Domain.Machines;

// Spec M4 2.5: the machine's time line as segments [StartAt, EndAt), one state each: what M5 adds up for
// availability. Built when the state changes (close the open segment, open the next, one transaction), never
// rebuilt from events at report time. At most one open segment per machine: a filtered unique index.
public class MachineStateHistory
{
    public long Id { get; private set; }
    public int MachineId { get; private set; }
    public MachineState State { get; private set; }
    public DateTime StartAt { get; private set; }
    public DateTime? EndAt { get; private set; }         // null = the current state
    public int? DowntimeReasonId { get; private set; }   // M5: why it stopped; null on a stop = "to classify"

    private MachineStateHistory() { }

    public MachineStateHistory(int machineId, MachineState state, DateTime startAt)
    {
        MachineId = machineId;
        State = state;
        StartAt = startAt;
    }

    public bool IsOpen => EndAt is null;

    public void Close(DateTime endAt)
    {
        if (!IsOpen) throw new DomainException("This state segment is already closed.");
        // Segments must not overlap: a message older than the open segment only goes to MachineEvent.
        if (endAt < StartAt) throw new DomainException("A state segment cannot end before it starts.");
        EndAt = endAt;
    }

    // Spec M5 1.2.4: the reason decides the OEE maths (CountsAgainstAvailability). Who may set it and when: step 6.
    public void AssignReason(int downtimeReasonId)
    {
        if (downtimeReasonId <= 0) throw new DomainException("A downtime reason id must be positive.");
        if (State == MachineState.Running) throw new DomainException("A running segment has no downtime reason.");
        DowntimeReasonId = downtimeReasonId;
    }
}
