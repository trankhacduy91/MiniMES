using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using MiniMes.Application.Common;
using MiniMes.Domain.Common;
using MiniMes.Domain.Quality;
using MiniMes.Domain.Quality.Spc;

namespace MiniMes.Application.Quality.Spc;

public sealed class SpcOptions
{
    public const string SectionName = "Spc";

    // [v2] spec B5: run of 7 (Western Electric says 8, Nelson 9). WE3 is in the engine but off (more false alarms).
    public int RunLength { get; set; } = WesternElectric.DefaultRunLength;
    public bool EnableWe3 { get; set; }

    public IReadOnlyList<WeRule> Rules()
        => EnableWe3 ? [.. WesternElectric.Default(RunLength), WesternElectric.We3] : WesternElectric.Default(RunLength);
}

// [v2] Spec M7 2.3 steps 4-8, shared by the inspection use case (step 5) and the machine sampler (step 6): evaluate
// the rules at ONE new point against the fixed baseline and open an alert per rule that fired, unless one is open.
public interface ISpcEvaluator
{
    // In the caller's transaction; the point is saved already (it needs its Id). Returns the alerts it opened.
    Task<IReadOnlyList<SpcAlert>> EvaluateAsync(IDbContextTransaction tx, Measurement point, CancellationToken ct);
}

public sealed class SpcEvaluator(IAppDbContext db, IOptions<SpcOptions> options, TimeProvider time) : ISpcEvaluator
{
    private const string Savepoint = "spc_alert";

    public async Task<IReadOnlyList<SpcAlert>> EvaluateAsync(IDbContextTransaction tx, Measurement point, CancellationToken ct)
    {
        // Q3: the stored baseline, never recomputed here. No baseline yet = points are kept, nothing is judged.
        var limits = await db.ControlLimitSets.AsNoTracking()
            .Where(c => c.InspectionPlanId == point.InspectionPlanId && c.IsActive)
            .Select(c => new { c.Mean, c.Sigma }).SingleOrDefaultAsync(ct);
        if (limits is null) return [];

        // The window that ENDS at this point, by measuring time (Q4; Id breaks ties). A late retry with an older
        // MeasuredAt is judged among its neighbours in time, not as if it were the newest.
        var rules = options.Value.Rules();
        var values = await db.Measurements.AsNoTracking()
            .Where(m => m.InspectionPlanId == point.InspectionPlanId
                        && (m.MeasuredAt < point.MeasuredAt || (m.MeasuredAt == point.MeasuredAt && m.Id <= point.Id)))
            .OrderByDescending(m => m.MeasuredAt).ThenByDescending(m => m.Id)
            .Take(WesternElectric.WindowSize(rules))
            .Select(m => m.Value).ToListAsync(ct);
        values.Reverse();
        var fired = WesternElectric.EvaluateLatest(values.Select(v => (double)v).ToList(), (double)limits.Mean, (double)limits.Sigma, rules);

        var opened = new List<SpcAlert>();
        foreach (var rule in fired)
        {
            // Q5: while an alert for this rule is open, the 8th and 9th point of the same run add nothing.
            if (await db.SpcAlerts.AnyAsync(a => a.InspectionPlanId == point.InspectionPlanId && a.RuleCode == rule
                                                 && a.Status == SpcAlertStatus.Open, ct))
                continue;
            if (await TryOpenAsync(tx, new SpcAlert(point.InspectionPlanId, rule, point.Id, time.GetUtcNow().UtcDateTime), ct) is { } alert)
                opened.Add(alert);
        }
        return opened;
    }

    // Two stations measuring at once both see "none open" (2.3 trap 2): the filtered unique index keeps one. The
    // loser's insert is undone back to the savepoint (its measurement and scan stay) and it simply has no new alert.
    private async Task<SpcAlert?> TryOpenAsync(IDbContextTransaction tx, SpcAlert alert, CancellationToken ct)
    {
        await tx.CreateSavepointAsync(Savepoint, ct);
        db.SpcAlerts.Add(alert);
        try
        {
            await db.SaveChangesAsync(ct);
            return alert;
        }
        catch (ConflictException)
        {
            await tx.RollbackToSavepointAsync(Savepoint, ct);
            db.Entry(alert).State = EntityState.Detached;
            return null;
        }
    }
}
