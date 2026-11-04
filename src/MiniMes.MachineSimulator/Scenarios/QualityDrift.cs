using MiniMes.MachineSimulator.Config;

namespace MiniMes.MachineSimulator.Scenarios;

// [v2] Spec M7 step 6 (a): the value an automatic tester measures on each board, ~ N(mean, σ) like a stable process
// (3V3 rail: 3.302 / 0.040). With "--scenario drift --after 100 --shift 0.02" the mean moves by +0.02 V (0.5σ) from
// board 101 on: every board is still well inside the spec (3.135-3.465), only the run rule can see it (spec 1.2).
// Seeded Random: the same seed gives the same series, so a demo (or a bug) can be replayed.
public sealed class QualityDrift(InspectionOptions inspection, DriftScenario? drift, Random rng)
{
    private int _boards;

    public int Boards => _boards;

    public decimal Next()
    {
        var shift = drift is { } d && _boards >= d.After ? d.Shift : 0;
        _boards++;
        var value = inspection.Mean + shift + inspection.Sigma * StandardNormal();
        return Math.Round((decimal)value, inspection.Decimals);   // a meter shows a fixed number of decimals
    }

    // Box-Muller: two uniform numbers -> one standard normal. 1 - NextDouble() is in (0, 1], so the log is finite.
    private double StandardNormal()
        => Math.Sqrt(-2.0 * Math.Log(1.0 - rng.NextDouble())) * Math.Cos(2.0 * Math.PI * rng.NextDouble());
}
