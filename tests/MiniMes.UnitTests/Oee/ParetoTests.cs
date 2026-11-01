using MiniMes.Application.Oee.Downtime;
using MiniMes.Contracts.Oee;

namespace MiniMes.UnitTests.Oee;

// Spec 1.2.9: week 40 of L1-SMT-01, 700 minutes of downtime.
public class ParetoTests
{
    private static readonly (string, string, bool, double, int)[] Week40 =
    [
        ("QC-WAIT", "Waiting for QC", true, 70, 5), ("FDR-JAM", "Feeder jam", true, 280, 30),
        ("OTHER", "Other", true, 70, 9), ("MAT-WAIT", "Waiting for material", true, 175, 7),
        ("CHG-MODEL", "Model changeover", true, 105, 3),
    ];

    [Fact]
    public void Ranked_by_minutes_with_percent_and_running_percent()
    {
        var p = Pareto.Rank(new(2026, 9, 28), new(2026, 10, 4), ParetoBy.Minutes, Week40);

        Assert.Equal(["FDR-JAM", "MAT-WAIT", "CHG-MODEL", "OTHER", "QC-WAIT"], p.Rows.Select(r => r.Reason));   // 70 = 70: by code
        Assert.Equal([40.0, 25.0, 15.0, 10.0, 10.0], p.Rows.Select(r => r.Percent));
        Assert.Equal([40.0, 65.0, 80.0, 90.0, 100.0], p.Rows.Select(r => r.CumulativePercent));
        Assert.Equal((700.0, 54), (p.TotalMinutes, p.TotalCount));
    }

    // By count the small repeated jams come first: a different action (standard work, not maintenance).
    [Fact]
    public void Ranked_by_count_shows_what_repeats()
    {
        var p = Pareto.Rank(new(2026, 9, 28), new(2026, 10, 4), ParetoBy.Count, Week40);

        Assert.Equal(["FDR-JAM", "OTHER", "MAT-WAIT", "QC-WAIT", "CHG-MODEL"], p.Rows.Select(r => r.Reason));
        Assert.Equal(55.56, p.Rows[0].Percent);
    }

    [Fact]
    public void No_stop_gives_an_empty_pareto()
        => Assert.Equal((0.0, 0), (Pareto.Rank(new(2026, 9, 28), new(2026, 9, 28), ParetoBy.Minutes, []).TotalMinutes,
            Pareto.Rank(new(2026, 9, 28), new(2026, 9, 28), ParetoBy.Minutes, []).TotalCount));
}
