namespace MiniMes.Domain.Execution;

// Yield formulas of spec M3 1.2, as percentages rounded to 2 decimals. null = nothing to divide by yet
// (no board entered the step, no board finished), which is not the same as 0 %.
public static class Yield
{
    public static decimal? Percent(int passed, int total) => total == 0 ? null : Math.Round(100m * passed / total, 2);

    // Final yield counts only finished boards: work in process is neither good nor bad yet, and putting it in the
    // denominator would drag the yield down in the middle of a shift.
    public static decimal? FinalYield(int completed, int scrapped) => Percent(completed, completed + scrapped);

    // RTY = FPY_step1 x FPY_step2 x ...: the chance a board crosses the whole line without any NG.
    // Over the steps that have seen boards; null when none has.
    public static decimal? Rolled(IEnumerable<(int PassedFirstTime, int Entered)> steps)
    {
        var withBoards = steps.Where(s => s.Entered > 0).ToList();
        if (withBoards.Count == 0) return null;
        var product = withBoards.Aggregate(1m, (p, s) => p * s.PassedFirstTime / s.Entered);
        return Math.Round(100m * product, 2);
    }
}
