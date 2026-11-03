namespace MiniMes.Domain.Quality.Spc;

public sealed record SpcStats(int Count, double Mean, double Sigma, double Ucl, double Lcl);

// Null = cannot be computed: that side has no specification limit, or σ = 0.
public sealed record Capability(double? Cp, double? Cpu, double? Cpl, double? Cpk);

// Spec M7 2.1: the SPC statistics as pure functions, unit tested against the hand calculation of 1.2
// (docs/quality-erd.md section 1). double here; specification checks stay decimal (Q2, InspectionPlan.Judge).
public static class SpcCalculator
{
    // Moving ranges of 2 points (I-MR chart): UCL_MR = D4 × MR̄. Same value as ControlLimitSet.D4.
    public const double D4 = 3.267;

    // Sample σ, divided by n − 1 (Bessel): the data is a sample of the process, not all of it. Dividing by n
    // gives 0.03194 instead of 0.03367 on the 1.2 example, ~5 % narrower limits. The MVP uses this σ for the
    // limits; textbook I charts estimate σ = MR̄ / 1.128 instead (spec 2.1 trap 3, section 4).
    public static SpcStats Compute(IReadOnlyList<double> values)
    {
        RequireTwoFinite(values);
        // All equal: σ is exactly 0. Computed, the mean of ten 3.3 can come out 3.3000000000000003 and σ a tiny
        // positive number, and the capability would then claim Cpk = 10^14 instead of "cannot be computed".
        if (values.All(v => v == values[0])) return new SpcStats(values.Count, values[0], 0, values[0], values[0]);
        var mean = values.Average();
        var sumSq = values.Sum(v => (v - mean) * (v - mean));
        var sigma = Math.Sqrt(sumSq / (values.Count - 1));
        return new SpcStats(values.Count, mean, sigma, mean + 3 * sigma, mean - 3 * sigma);
    }

    // [v2] I-MR: the average absolute difference between consecutive points, in the order they were measured.
    public static (double MrBar, double UclMr) MovingRange(IReadOnlyList<double> values)
    {
        RequireTwoFinite(values);
        var mrBar = Enumerable.Range(1, values.Count - 1).Average(i => Math.Abs(values[i] - values[i - 1]));
        return (mrBar, D4 * mrBar);
    }

    // Cp: is the process narrow enough (spec width / 6σ); Cpk: and is it centred (distance to the nearer limit / 3σ).
    // One-sided characteristic: no Cp, Cpk is the side that exists. σ = 0 (all values equal): nothing to divide by.
    public static Capability CalcCapability(SpcStats s, double? usl, double? lsl)
    {
        if (usl < lsl) throw new ArgumentException("USL is below LSL.", nameof(usl));
        if (s.Sigma == 0) return new Capability(null, null, null, null);

        var cpu = usl is { } u ? (u - s.Mean) / (3 * s.Sigma) : (double?)null;
        var cpl = lsl is { } l ? (s.Mean - l) / (3 * s.Sigma) : (double?)null;
        var cp = usl is { } u2 && lsl is { } l2 ? (u2 - l2) / (6 * s.Sigma) : (double?)null;
        var cpk = cpu is null ? cpl : cpl is null ? cpu : Math.Min(cpu.Value, cpl.Value);
        return new Capability(cp, cpu, cpl, cpk);
    }

    // n < 2 has no spread to speak of; NaN/∞ (a broken reading that slipped through) would poison every result.
    private static void RequireTwoFinite(IReadOnlyList<double> values)
    {
        if (values.Count < 2) throw new ArgumentException("Need at least 2 values.", nameof(values));
        if (values.Any(v => !double.IsFinite(v))) throw new ArgumentException("Values must be finite numbers.", nameof(values));
    }
}
