using MiniMes.Domain.Oee;
using MiniMes.Domain.Time;

namespace MiniMes.Application.Oee;

public sealed class OeeOptions
{
    public const string SectionName = "Oee";

    // Spec M5 1.2.5: stops shorter than this are micro stops (no reason needed, they show in Performance).
    // Keep it equal to Andon:AutoRaiseAfterSeconds so nobody is called for a stop that does not count.
    public int MicroStopThresholdSec { get; set; } = 60;
}

/// <param name="Input">Totals for OeeCalculator.</param>
/// <param name="ScanCount">Scan rows at the machine's station(s) in the window: the cross-check with the machine
/// counter (step 9) compares it with Input's total count.</param>
public sealed record OeeReading(OeeInput Input, long ScanCount);

// Spec M5 step 4: the I/O half of OEE. Reads M3/M4 data for one machine and window and hands back totals;
// the maths stays in the pure OeeCalculator.
public interface IOeeInputReader
{
    /// <param name="shift">The shift the window lies in: its breaks, and whether the machine had work that shift.</param>
    /// <param name="window">An hour or the whole shift; a running shift is already cut at "now" by the caller.</param>
    Task<OeeReading> ReadAsync(int machineId, ShiftWindow shift, Interval window, CancellationToken ct);
}
