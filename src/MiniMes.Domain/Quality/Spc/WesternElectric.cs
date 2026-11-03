namespace MiniMes.Domain.Quality.Spc;

// Spec M7 2.2: every Western Electric rule is "N of the last M points beyond K σ, on the same side of the center
// line". One generic check, configured by data; a rule outside this shape (a trend) would be a class of its own.
public sealed record WeRule(SpcRuleCode Code, int N, int M, double K)
{
    public WeRule Validated()
    {
        if (N < 1 || M < N) throw new ArgumentException($"Rule {Code}: need 1 <= N <= M (N = {N}, M = {M}).");
        if (K < 0) throw new ArgumentException($"Rule {Code}: K cannot be negative.");
        return this;
    }
}

// Index = the point that completes the pattern (the last of the window): the alert is tied to it (Q5) and the
// chart paints it red.
public sealed record RuleViolation(SpcRuleCode Rule, int Index);

public static class WesternElectric
{
    public const int DefaultRunLength = 7;

    // [v2] optional, off by default: every extra rule adds false alarms (spec 1.2, "why only 3 rules").
    public static readonly WeRule We3 = new(SpcRuleCode.WE3, 4, 5, 1.0);

    // [v2] the three default rules; the run length is configurable (7 here, 8 or 9 in other textbooks).
    public static IReadOnlyList<WeRule> Default(int runLength = DefaultRunLength) =>
    [
        new(SpcRuleCode.WE1, 1, 1, 3.0),                       // 1 point beyond 3σ
        new(SpcRuleCode.WE2, 2, 3, 2.0),                       // 2 of 3 beyond 2σ, same side
        new WeRule(SpcRuleCode.WE4, runLength, runLength, 0.0).Validated(),   // run: N in a row on one side
    ];

    // The points, oldest first (by MeasuredAt, 2.3 trap 1), against a FIXED baseline (Q3). Every violation at every
    // point: a long run reports each point from the 7th on; the use case keeps one open alert per rule (Q5).
    public static IReadOnlyList<RuleViolation> Evaluate(IReadOnlyList<double> values, double mean, double sigma,
        IEnumerable<WeRule> rules)
    {
        // σ = 0 makes every z infinite; ControlLimitSet already refuses it, this is the second line.
        if (!(sigma > 0) || !double.IsFinite(sigma)) throw new ArgumentException("Sigma must be a positive number.", nameof(sigma));
        if (!double.IsFinite(mean)) throw new ArgumentException("Mean must be a finite number.", nameof(mean));
        var checkedRules = rules.Select(r => r.Validated()).ToList();
        var z = values.Select(v => Z(v, mean, sigma)).ToArray();

        var result = new List<RuleViolation>();
        foreach (var rule in checkedRules)
            for (var i = 0; i < z.Length; i++)
                if (Fires(z, i, rule)) result.Add(new RuleViolation(rule.Code, i));
        return result;
    }

    // Online use (2.3): only the newest point matters, so only the last max(M) points are needed.
    public static IReadOnlyList<SpcRuleCode> EvaluateLatest(IReadOnlyList<double> values, double mean, double sigma,
        IEnumerable<WeRule> rules)
    {
        var last = values.Count - 1;
        return Evaluate(values, mean, sigma, rules).Where(v => v.Index == last).Select(v => v.Rule).ToList();
    }

    public static int WindowSize(IEnumerable<WeRule> rules) => rules.Max(r => r.M);

    // Point i fires when it is beyond K on one side itself, and the window ending at i holds N such points on that
    // side. At the start the window is shorter than M: allowed (two first points beyond 2σ are a real WE2), a run
    // still needs N points. "Beyond" is strict: a point on the line is not beyond it, and z = 0 (on the center
    // line) belongs to neither side, so it breaks a run.
    private static bool Fires(double[] z, int i, WeRule rule)
    {
        foreach (var side in (ReadOnlySpan<int>)[1, -1])
        {
            if (side * z[i] <= rule.K) continue;
            var count = 0;
            for (var j = Math.Max(0, i - rule.M + 1); j <= i; j++)
                if (side * z[j] > rule.K) count++;
            if (count >= rule.N) return true;
        }
        return false;
    }

    // Rounded to 9 decimals: a value exactly on UCL (3.422 with 3.302 / 0.040) computes to z = 3.0000000000000027
    // in double and would count as beyond it. Measurements are decimal(18,6), so 9 decimals of z lose nothing real.
    private static double Z(double value, double mean, double sigma)
    {
        if (!double.IsFinite(value)) throw new ArgumentException("Values must be finite numbers.");
        return Math.Round((value - mean) / sigma, 9);
    }
}
