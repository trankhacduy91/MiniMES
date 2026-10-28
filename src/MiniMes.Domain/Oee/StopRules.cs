using MiniMes.Domain.Machines;

namespace MiniMes.Domain.Oee;

public enum StopTreatment
{
    Run,          // in Run Time
    NotPlanned,   // removed from Planned Production Time: no Availability loss
    Downtime,     // unplanned: an Availability loss (unless it is a micro stop or inside a break)
}

// Spec M5 1.2.4: every machine state goes one of two ways, written down once so two people get one OEE.
// Only CountsAgainstAvailability decides the maths; the reason's Category is for reports.
public static class StopRules
{
    /// <param name="reasonCountsAgainstAvailability">The segment's downtime reason; null = no reason yet.</param>
    /// <param name="hasPlan">The machine had work in this shift (its station scanned at least once). An Idle machine
    /// with nothing planned is not judged; one that waits while a WO runs is a loss (MAT-WAIT / upstream).</param>
    public static StopTreatment Treat(MachineState state, bool? reasonCountsAgainstAvailability, bool hasPlan)
    {
        if (state == MachineState.Running) return StopTreatment.Run;
        if (state == MachineState.PlannedStop) return StopTreatment.NotPlanned;
        // A reason a supervisor chose wins (PM on a Down = planned maintenance, not a breakdown).
        if (reasonCountsAgainstAvailability is { } counts) return counts ? StopTreatment.Downtime : StopTreatment.NotPlanned;
        return state switch
        {
            MachineState.Idle => hasPlan ? StopTreatment.Downtime : StopTreatment.NotPlanned,
            // Down, Setup, Offline without a reason: still a loss (rule 7), or leaving it empty would pay off.
            // Offline = "no data", shown in the to-classify list for the supervisor to confirm.
            _ => StopTreatment.Downtime,
        };
    }
}
