extern alias simulator;
using MiniMes.Domain.Quality.Spc;
using simulator::MiniMes.MachineSimulator.Config;
using simulator::MiniMes.MachineSimulator.Scenarios;

namespace MiniMes.IntegrationTests.Quality;

// [v2] Spec M7 step 6 (a) check: "200 boards with drift -> the run rule fires within ~10-20 boards after the shift".
// The simulator's tester values (--scenario drift --after 100 --shift 0.02) through the MES rule engine against the
// seeded 3V3_RAIL baseline, no database: the question is statistical, so it is asked over many seeds.
public class QualityDriftTests
{
    private static readonly InspectionOptions Rail = new() { PlanCode = "3V3_RAIL", Mean = 3.302, Sigma = 0.040, Decimals = 3 };
    private const double BaselineMean = 3.302, BaselineSigma = 0.040, Usl = 3.465, Lsl = 3.135;

    // Index (0-based board) of the earliest rule violation at or after board `from`. Min, not First: Evaluate lists the
    // violations rule by rule (all WE1, then WE2...), not in board order.
    private static int? FirstAlertFrom(IReadOnlyList<double> values, int from)
        => WesternElectric.Evaluate(values, BaselineMean, BaselineSigma, WesternElectric.Default())
            .Where(v => v.Index >= from).Select(v => (int?)v.Index).Min();

    private static double[] Series(int seed, DriftScenario? drift, int boards = 200)
    {
        var meter = new QualityDrift(Rail, drift, new Random(seed));
        return Enumerable.Range(0, boards).Select(_ => (double)meter.Next()).ToArray();
    }

    // Boards from the shift to the first alert, over 500 seeds (an independent Python simulation of the same rules
    // gives median 19, p25 9). Spec: ~10-20 boards. 0.02 V is 0.5σ: half the boards still sit within 1σ of the old mean.
    [Fact]
    public void After_a_half_sigma_shift_the_rules_fire_within_about_ten_to_twenty_boards()
    {
        var delays = Delays(new DriftScenario(100, 0.02));

        Assert.InRange(delays[delays.Length / 2], 14, 26);   // measured: median 20
        Assert.InRange(delays[delays.Length / 4], 6, 14);    // measured: p25 10
    }

    // Without the shift the same rules also fire sometimes (false alarms: 3 rules, ARL of a few dozen boards). The
    // drift is what makes them come three times sooner (measured median 61 vs 20).
    [Fact]
    public void Without_the_shift_alerts_come_much_later()
    {
        var withDrift = Delays(new DriftScenario(100, 0.02));
        var without = Delays(null);

        Assert.True(without[without.Length / 2] >= 2 * withDrift[withDrift.Length / 2],
            $"median without {without[without.Length / 2]}, with drift {withDrift[withDrift.Length / 2]}");
    }

    // Board count (1 = the first shifted board) to the first violation, sorted; 999 = none in 300 boards.
    private static int[] Delays(DriftScenario? drift)
        => Enumerable.Range(1, 500)
            .Select(seed => FirstAlertFrom(Series(seed, drift, 400), 100) is { } i ? i - 99 : 999)
            .Order().ToArray();

    [Fact]
    public void The_same_seed_gives_the_same_series()
        => Assert.Equal(Series(7, null, 20), Series(7, null, 20));

    [Fact]
    public void Values_have_the_meter_resolution()
        => Assert.All(Series(3, null, 50), v => Assert.Equal(Math.Round(v, 3), v));
}
