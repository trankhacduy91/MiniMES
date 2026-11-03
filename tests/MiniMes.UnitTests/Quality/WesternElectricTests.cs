using MiniMes.Domain.Quality;
using MiniMes.Domain.Quality.Spc;

namespace MiniMes.UnitTests.Quality;

// Spec M7 step 4: the rule examples of 1.2, on the 3V3 baseline mean 3.302 / σ 0.040
// (zone lines: ±1σ 3.342 / 3.262, ±2σ 3.382 / 3.222, ±3σ 3.422 / 3.182).
public class WesternElectricTests
{
    private const double Mean = 3.302, Sigma = 0.040;

    // Spec 1.2 WE4 table: 8 points in Zone C, all above the center line, all inside the spec.
    private static readonly double[] Run8 = [3.318, 3.325, 3.311, 3.322, 3.309, 3.330, 3.316, 3.321];

    private static IReadOnlyList<RuleViolation> Eval(double[] x, IEnumerable<WeRule>? rules = null)
        => WesternElectric.Evaluate(x, Mean, Sigma, rules ?? WesternElectric.Default());

    // WE1: 3.430 is z = +3.2 (and still inside USL 3.465); 3.170 is z = −3.3.
    [Theory]
    [InlineData(3.430)]
    [InlineData(3.170)]
    public void We1_fires_on_one_point_beyond_3_sigma(double value)
        => Assert.Equal([new RuleViolation(SpcRuleCode.WE1, 0)], Eval([value]));

    // WE2: 3.390 / 3.310 / 3.400 = +2.2 / +0.2 / +2.45: two of three beyond +2σ, fires at the third point only.
    [Fact]
    public void We2_fires_on_two_of_three_beyond_2_sigma_at_the_third_point()
        => Assert.Equal([new RuleViolation(SpcRuleCode.WE2, 2)], Eval([3.390, 3.310, 3.400]));

    // Same side only: +2.2 and −2.3 are each beyond 2σ, but on opposite sides.
    [Fact]
    public void We2_does_not_mix_the_two_sides()
        => Assert.Empty(Eval([3.390, 3.310, 3.210]));

    // A short window at the start is allowed: the first two points beyond 2σ are a real WE2.
    [Fact]
    public void We2_may_fire_at_the_start_of_the_series()
        => Assert.Equal([new RuleViolation(SpcRuleCode.WE2, 1)], Eval([3.390, 3.400]));

    // [v2] run 7: fires at the 7th point and nothing else does.
    [Fact]
    public void Seven_points_above_the_center_fire_the_run_rule_at_the_last_point()
    {
        var only = Assert.Single(Eval(Run8[..7]));
        Assert.Equal(new RuleViolation(SpcRuleCode.WE4, 6), only);
    }

    [Fact]
    public void Six_points_above_the_center_do_not_fire()
        => Assert.Empty(Eval(Run8[..6]));

    // The function reports every point from the 7th on; one alert per rule is the use case's job (Q5, step 5).
    [Fact]
    public void A_longer_run_reports_each_further_point()
        => Assert.Equal([6, 7], Eval(Run8).Select(v => v.Index));

    // Below the center works the same way (mirror of the table).
    [Fact]
    public void Seven_points_below_the_center_fire_too()
        => Assert.Equal([new RuleViolation(SpcRuleCode.WE4, 6)], Eval(Run8[..7].Select(v => 2 * Mean - v).ToArray()));

    // z = 0 belongs to neither side: a point exactly on the center line breaks the run (3 above, center, 4 above).
    [Fact]
    public void A_point_on_the_center_line_breaks_the_run()
    {
        double[] broken = [3.318, 3.325, 3.311, Mean, 3.322, 3.309, 3.330, 3.316];
        Assert.Empty(Eval(broken));
        Assert.Single(Eval(broken.Where(v => v != Mean).ToArray()));               // without it: 7 in a row, fires
    }

    // "Beyond" is strict. 3.422 is exactly on UCL, but (3.422 − 3.302) / 0.040 = 3.0000000000000027 in double:
    // without rounding z it would fire. One micro-volt more does fire.
    [Fact]
    public void A_point_exactly_on_a_zone_line_is_not_beyond_it()
    {
        Assert.Empty(Eval([3.422]));
        Assert.Empty(Eval([3.182]));
        Assert.Empty(Eval([3.382, 3.300, 3.382]));                                 // on +2σ twice: no WE2
        Assert.Equal([SpcRuleCode.WE1], Eval([3.422001]).Select(v => v.Rule));
    }

    // [v2] WE3 is in the engine but off by default. Spec 1.2: 3.350 / 3.360 / 3.310 / 3.370 / 3.350
    // = +1.2 / +1.45 / +0.2 / +1.7 / +1.2: four of five beyond +1σ, fires at the fifth point.
    [Fact]
    public void We3_fires_only_when_switched_on()
    {
        double[] x = [3.350, 3.360, 3.310, 3.370, 3.350];

        Assert.Empty(Eval(x));
        Assert.Equal([new RuleViolation(SpcRuleCode.WE3, 4)], Eval(x, WesternElectric.Default().Append(WesternElectric.We3)));
    }

    // [v2] RunLength is configuration: with 8 (Western Electric's own number) the 7-point run is quiet.
    [Fact]
    public void Run_length_is_configurable()
    {
        Assert.Empty(Eval(Run8[..7], WesternElectric.Default(runLength: 8)));
        Assert.Equal([new RuleViolation(SpcRuleCode.WE4, 7)], Eval(Run8, WesternElectric.Default(runLength: 8)));
    }

    // Online (2.3): only violations at the newest point count; older ones were handled when they were new.
    [Fact]
    public void EvaluateLatest_reports_only_the_newest_point()
    {
        Assert.Equal([SpcRuleCode.WE4], WesternElectric.EvaluateLatest(Run8, Mean, Sigma, WesternElectric.Default()));
        Assert.Empty(WesternElectric.EvaluateLatest([3.430, 3.300], Mean, Sigma, WesternElectric.Default()));   // WE1 was the point before
    }

    // The use case loads only this many recent points (2.2 trap 5).
    [Fact]
    public void Window_size_is_the_longest_rule()
    {
        Assert.Equal(7, WesternElectric.WindowSize(WesternElectric.Default()));
        Assert.Equal(9, WesternElectric.WindowSize(WesternElectric.Default(runLength: 9)));
    }

    // Rules are independent: 3.390 then 3.430 is a WE2 (two beyond +2σ) and a WE1 at the same point.
    [Fact]
    public void Several_rules_can_fire_at_the_same_point()
        => Assert.Equal([SpcRuleCode.WE1, SpcRuleCode.WE2],
            Eval([3.390, 3.430]).Where(v => v.Index == 1).Select(v => v.Rule));

    [Theory]
    [InlineData(0.0)]
    [InlineData(-0.04)]
    [InlineData(double.NaN)]
    public void Sigma_must_be_positive(double sigma)
        => Assert.Throws<ArgumentException>(() => WesternElectric.Evaluate([3.3, 3.31], Mean, sigma, WesternElectric.Default()));

    [Fact]
    public void A_rule_needs_one_to_m_points()
    {
        Assert.Throws<ArgumentException>(() => Eval([3.3], [new WeRule(SpcRuleCode.WE2, 3, 2, 2.0)]));   // N > M
        Assert.Throws<ArgumentException>(() => WesternElectric.Default(runLength: 0));
        Assert.Throws<ArgumentException>(() => Eval([double.NaN]));
    }
}
