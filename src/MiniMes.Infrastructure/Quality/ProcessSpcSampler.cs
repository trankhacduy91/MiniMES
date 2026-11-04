using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MiniMes.Application.Andon;
using MiniMes.Application.Common;
using MiniMes.Application.Quality.Spc;
using MiniMes.Application.Realtime;
using MiniMes.Contracts.Quality;
using MiniMes.Domain.Andon;
using MiniMes.Domain.Common;
using MiniMes.Domain.Machines;
using MiniMes.Domain.Quality;
using MiniMes.Domain.Quality.Spc;

namespace MiniMes.Infrastructure.Quality;

public enum SampleOutcome { Skipped, NotDue, Waiting, Taken, Duplicate, Gap }

// [v2] Spec M7 2.3b: SPC from a machine's process tag (the reflow peak temperature). Every 10 s, for each tag linked
// to a characteristic (MachineTag.SpcCharacteristicId, M4): when its period is due, the latest Good value after the
// previous sample becomes a Measurement (Source = Machine, no serial) and goes through the same ISpcEvaluator as the
// inspection use case. Beyond USL/LSL: an OutOfSpec alert + an Andon Quality call (Q18: no board to NG). No new value
// for 3 periods (machine offline, tag Bad): a DataGap alert, never a made-up point. The rhythm itself is
// SpcSampling.Decide (Domain, unit tested); this class reads, writes and ticks.
public sealed class ProcessSpcSampler(IServiceScopeFactory scopes, TimeProvider clock, IOptions<SpcOptions> options,
    ILogger<ProcessSpcSampler> log) : BackgroundService
{
    public const string SystemUser = "system";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.Value.SamplerTickSec), clock);
        do
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex) { log.LogError(ex, "SPC sampler failed, next try in {TickSec} s", options.Value.SamplerTickSec); }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    public async Task RunOnceAsync(CancellationToken ct)
    {
        List<int> tagIds;
        await using (var scope = scopes.CreateAsyncScope())
            tagIds = await scope.ServiceProvider.GetRequiredService<IAppDbContext>().MachineTags.AsNoTracking()
                .Where(t => t.IsActive && t.SpcCharacteristicId != null).Select(t => t.Id).ToListAsync(ct);
        foreach (var tagId in tagIds)
        {
            try { await SampleTagAsync(tagId, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "SPC sampling of tag {TagId} failed", tagId);   // one broken tag does not stop the others
            }
        }
    }

    // One tag, its own scope and transaction.
    public async Task<SampleOutcome> SampleTagAsync(int tagId, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        var tag = await db.MachineTags.AsNoTracking().SingleOrDefaultAsync(t => t.Id == tagId, ct);
        if (tag is not { IsActive: true, SpcCharacteristicId: { } planId, SpcSampleEverySec: { } everySec }) return SampleOutcome.Skipped;
        var plan = await db.InspectionPlans.AsNoTracking().SingleAsync(p => p.Id == planId, ct);
        if (!plan.IsActive) return SampleOutcome.Skipped;

        var now = clock.GetUtcNow().UtcDateTime;
        var last = await db.Measurements.Where(m => m.MachineTagId == tag.Id).MaxAsync(m => (DateTime?)m.SourceTs, ct);
        // Only Good values (a Bad one is a broken sensor, M4), newer than the last sample. Not from the future: a
        // machine clock running ahead would put every later point before it; the gap alert shows that instead.
        var value = await db.ProcessValues.AsNoTracking()
            .Where(v => v.MachineTagId == tag.Id && v.Quality == DataQuality.Good && (last == null || v.SourceTs > last) && v.SourceTs <= now)
            .OrderByDescending(v => v.SourceTs).FirstOrDefaultAsync(ct);

        switch (SpcSampling.Decide(now, last, everySec, value is not null, options.Value.GapPeriods))
        {
            case SampleDecision.NotDue: return SampleOutcome.NotDue;
            case SampleDecision.Wait: return SampleOutcome.Waiting;
            case SampleDecision.Gap:
                if (await RaiseAloneAsync(scope, db, plan.Id, SpcRuleCode.DataGap, ct) is { } gapAlert)
                {
                    log.LogWarning("SPC data gap on {PlanCode}: no Good value of tag {TagId} since {LastSample:o}", plan.Code, tag.Id, last);
                    await PushAsync(scope, gapAlert, plan.Code, ct);
                }
                return SampleOutcome.Gap;
        }

        var evaluator = scope.ServiceProvider.GetRequiredService<ISpcEvaluator>();
        var point = Measurement.FromMachine(plan, tag.Id, Math.Round((decimal)value!.Value, 6), value.SourceTs);
        List<SpcAlert> opened = [];
        await using (var tx = await db.BeginTransactionAsync(ct))
        {
            db.Measurements.Add(point);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (ConflictException)   // the same machine value sampled twice (a second sampler, a rerun): one point
            {
                return SampleOutcome.Duplicate;
            }
            opened.AddRange(await evaluator.EvaluateAsync(tx, point, ct));
            if (point.Result == MeasureResult.Ng && await evaluator.RaiseAsync(tx, plan.Id, SpcRuleCode.OutOfSpec, point.Id, ct) is { } oos)
                opened.Add(oos);
            // Samples flow again: the gap is over.
            foreach (var gap in await db.SpcAlerts.Where(a => a.InspectionPlanId == plan.Id && a.RuleCode == SpcRuleCode.DataGap
                                                              && a.Status == SpcAlertStatus.Open).ToListAsync(ct))
                gap.Close(SystemUser, "Samples resumed.", now);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }

        foreach (var a in opened)
        {
            log.LogWarning("SPC alert {RuleCode} on {PlanCode} at {Value} {Unit} ({SourceTs:o})", a.RuleCode, plan.Code, point.Value, plan.Unit, point.SourceTs);
            await PushAsync(scope, a, plan.Code, ct);
        }
        // After the commit, its own transaction: the point is kept even if the Andon cannot be raised. RaiseAndon
        // answers the open call when one is there already, so a long excursion is one call, not one per sample.
        if (point.Result == MeasureResult.Ng)
            await scope.ServiceProvider.GetRequiredService<ISender>()
                .Send(new RaiseAndonCommand(tag.MachineId, nameof(AndonType.Quality), Auto: true), ct);
        return SampleOutcome.Taken;
    }

    private static Task PushAsync(AsyncServiceScope scope, SpcAlert a, string planCode, CancellationToken ct)
        => scope.ServiceProvider.GetRequiredService<IRealtimeNotifier>().SpcAlertRaisedAsync(
            new SpcAlertDto(a.Id, a.InspectionPlanId, planCode, a.RuleCode.ToString(), a.MeasurementId, a.RaisedAt, a.Status.ToString(), a.NcrId), ct);

    private static async Task<SpcAlert?> RaiseAloneAsync(AsyncServiceScope scope, IAppDbContext db, int planId, SpcRuleCode rule, CancellationToken ct)
    {
        await using var tx = await db.BeginTransactionAsync(ct);
        var alert = await scope.ServiceProvider.GetRequiredService<ISpcEvaluator>().RaiseAsync(tx, planId, rule, null, ct);
        await tx.CommitAsync(ct);
        return alert;
    }
}
