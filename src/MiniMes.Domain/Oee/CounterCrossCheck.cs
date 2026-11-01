namespace MiniMes.Domain.Oee;

// Spec M5 1.2.10 / rule 10: Total comes from the machine counter, Good from the serial scans. Two independent
// sources, so they can be compared: |machine - scan rows| / machine > 2 % means one of them is wrong (the machine
// counts dry cycles, scans were skipped, a simulator bug), and then Q and OEE cannot be trusted. Flag, never "fix".
// Scan rows, not distinct serials: a board tested again is a second pass for both sources.
public static class CounterCrossCheck
{
    public const double Tolerance = 0.02;

    // null when the machine counted nothing (no denominator).
    public static double? Gap(long machineTotal, long scanCount)
        => machineTotal > 0 ? Math.Abs(machineTotal - scanCount) / (double)machineTotal : null;

    // Nothing counted and nothing scanned agrees; scans without a single count do not.
    public static bool IsMismatch(long machineTotal, long scanCount)
        => Gap(machineTotal, scanCount) is { } gap ? gap > Tolerance : scanCount > 0;

    // The flag is decided again from these totals: a combined result (line, shift) is judged on its own sums.
    public static OeeResult Apply(OeeResult r, long scanCount) => r with
    {
        Warnings = IsMismatch(r.TotalCount, scanCount)
            ? r.Warnings | OeeWarning.CounterMismatch
            : r.Warnings & ~OeeWarning.CounterMismatch,
    };
}
