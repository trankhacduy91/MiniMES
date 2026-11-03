using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MiniMes.Application.Common;
using MiniMes.Contracts.Quality;
using MiniMes.Domain.Common;
using MiniMes.Domain.Quality;
using MiniMes.Domain.Quality.Spc;

namespace MiniMes.Application.Quality.Spc;

internal static class QualityDtos
{
    public static SpcAlertDto Alert(SpcAlert a, string planCode)
        => new(a.Id, a.InspectionPlanId, planCode, a.RuleCode.ToString(), a.MeasurementId, a.RaisedAt, a.Status.ToString(), a.NcrId);

    public static ControlLimitsDto Limits(ControlLimitSet c)
        => new(c.Id, c.Mean, c.Sigma, c.Ucl, c.Lcl, c.MrBar, c.UclMr, c.SampleCount, c.BaselineFrom, c.BaselineTo, c.Reason,
            c.CalculatedBy, c.CalculatedAt);
}

// Spec M7 2.4: what the control chart draws. The latest Take points, oldest first, against the stored baseline.
public sealed record GetSpcChartQuery(int PlanId, int Take = 100) : IRequest<SpcChartDto>;

public sealed class GetSpcChartValidator : AbstractValidator<GetSpcChartQuery>
{
    public const int MaxTake = 500;
    public GetSpcChartValidator() => RuleFor(x => x.Take).InclusiveBetween(2, MaxTake);
}

public sealed class GetSpcChartHandler(IAppDbContext db, IOptions<SpcOptions> options) : IRequestHandler<GetSpcChartQuery, SpcChartDto>
{
    private sealed record Row(long Id, DateTime MeasuredAt, decimal Value, MeasureResult Result, string? SerialNo);

    public async Task<SpcChartDto> Handle(GetSpcChartQuery q, CancellationToken ct)
    {
        var plan = await db.InspectionPlans.AsNoTracking().SingleOrDefaultAsync(p => p.Id == q.PlanId, ct)
                   ?? throw new KeyNotFoundException($"Inspection plan {q.PlanId} not found.");
        var limits = await db.ControlLimitSets.AsNoTracking().SingleOrDefaultAsync(c => c.InspectionPlanId == plan.Id && c.IsActive, ct);
        var rules = options.Value.Rules();

        // A few points before the window as context: the first shown points are judged with the points that came
        // before them, as they were when measured, not as if the plan started there.
        var context = WesternElectric.WindowSize(rules) - 1;
        var rows = await (from m in db.Measurements.AsNoTracking()
                          where m.InspectionPlanId == plan.Id
                          join s in db.Serials on m.SerialId equals s.Id into sj
                          from s in sj.DefaultIfEmpty()
                          orderby m.MeasuredAt descending, m.Id descending
                          select new Row(m.Id, m.MeasuredAt, m.Value, m.Result, s.SerialNo))
            .Take(q.Take + context).ToListAsync(ct);
        rows.Reverse();
        var firstShown = Math.Max(0, rows.Count - q.Take);
        var shown = rows.Skip(firstShown).ToList();

        var violations = new List<SpcViolationDto>();
        if (limits is not null && rows.Count > 0)
            violations.AddRange(WesternElectric.Evaluate(rows.Select(r => (double)r.Value).ToList(), (double)limits.Mean, (double)limits.Sigma, rules)
                .Where(v => v.Index >= firstShown)
                .Select(v => new SpcViolationDto(rows[v.Index].Id, v.Rule.ToString())));

        var points = shown.Select((r, i) => new SpcPointDto(r.Id, r.MeasuredAt, r.Value, r.Result.ToString(), r.SerialNo,
            i == 0 ? null : Math.Abs((double)(r.Value - shown[i - 1].Value)))).ToList();

        var openAlerts = await db.SpcAlerts.AsNoTracking()
            .Where(a => a.InspectionPlanId == plan.Id && a.Status == SpcAlertStatus.Open)
            .OrderByDescending(a => a.RaisedAt).ToListAsync(ct);

        return new SpcChartDto(plan.Id, plan.Code, plan.Name, plan.Unit, plan.Lsl, plan.Usl, plan.Target,
            limits is null ? null : QualityDtos.Limits(limits), points, violations, WindowStats(shown, plan),
            openAlerts.Select(a => QualityDtos.Alert(a, plan.Code)).ToList());
    }

    // The current capability of the shown points against today's spec: what "Cp/Cpk now" on the chart means.
    private static SpcWindowStatsDto? WindowStats(List<Row> shown, InspectionPlan plan)
    {
        if (shown.Count < 2) return null;
        var values = shown.Select(r => (double)r.Value).ToList();
        var stats = SpcCalculator.Compute(values);
        var capability = SpcCalculator.CalcCapability(stats, (double?)plan.Usl, (double?)plan.Lsl);
        return new SpcWindowStatsDto(stats.Count, stats.Mean, stats.Sigma, SpcCalculator.MovingRange(values).MrBar, capability.Cp, capability.Cpk);
    }
}

// Q3: QC fixes a new Phase I baseline from a period the process was known to be stable, and says why. The old one
// stays as history (IsActive = false); nothing else ever moves the limits.
public sealed record RecomputeControlLimitsCommand(int PlanId, DateTime From, DateTime To, string Reason) : IRequest<ControlLimitsDto>;

public sealed class RecomputeControlLimitsValidator : AbstractValidator<RecomputeControlLimitsCommand>
{
    public RecomputeControlLimitsValidator()
    {
        RuleFor(x => x.To).GreaterThan(x => x.From).WithMessage("The baseline period must end after it starts.");
        RuleFor(x => x.Reason).NotEmpty().MaximumLength(ControlLimitSet.ReasonMaxLength);
    }
}

public sealed class RecomputeControlLimitsHandler(IAppDbContext db, ICurrentUser user, TimeProvider time)
    : IRequestHandler<RecomputeControlLimitsCommand, ControlLimitsDto>
{
    public const int MinPoints = 20;   // Q3: at least 20 points

    public async Task<ControlLimitsDto> Handle(RecomputeControlLimitsCommand cmd, CancellationToken ct)
    {
        var by = user.EmployeeCode ?? throw new InvalidOperationException("Recomputing limits needs a logged-in user.");
        if (!await db.InspectionPlans.AnyAsync(p => p.Id == cmd.PlanId, ct)) throw new KeyNotFoundException($"Inspection plan {cmd.PlanId} not found.");
        var (from, to) = (Utc(cmd.From), Utc(cmd.To));

        // In measuring order: the moving range depends on it.
        var values = await db.Measurements.AsNoTracking()
            .Where(m => m.InspectionPlanId == cmd.PlanId && m.MeasuredAt >= from && m.MeasuredAt < to)
            .OrderBy(m => m.MeasuredAt).ThenBy(m => m.Id).Select(m => m.Value).ToListAsync(ct);
        if (values.Count < MinPoints)
            throw new DomainException($"A baseline needs at least {MinPoints} points; the period has {values.Count}.");

        var doubles = values.Select(v => (double)v).ToList();
        var stats = SpcCalculator.Compute(doubles);
        var (mrBar, _) = SpcCalculator.MovingRange(doubles);
        // σ = 0 (every point equal) is refused by ControlLimitSet: such data cannot be a baseline.
        var next = new ControlLimitSet(cmd.PlanId, Round(stats.Mean), Round(stats.Sigma), Round(mrBar), values.Count, from, to,
            cmd.Reason, by, time.GetUtcNow().UtcDateTime);

        // Two saves in one transaction: the old row stops being active before the new one is inserted, so the
        // filtered unique index (one active per plan) never sees two. Two QC recomputing at once: one gets 409.
        await using var tx = await db.BeginTransactionAsync(ct);
        foreach (var old in await db.ControlLimitSets.Where(c => c.InspectionPlanId == cmd.PlanId && c.IsActive).ToListAsync(ct))
            old.Deactivate();
        await db.SaveChangesAsync(ct);
        db.ControlLimitSets.Add(next);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return QualityDtos.Limits(next);
    }

    private static decimal Round(double value) => Math.Round((decimal)value, 6);

    // JSON without "Z" arrives Unspecified: the API speaks UTC (CLAUDE.md), so read it as UTC.
    private static DateTime Utc(DateTime t) => t.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(t, DateTimeKind.Utc) : t.ToUniversalTime();
}
