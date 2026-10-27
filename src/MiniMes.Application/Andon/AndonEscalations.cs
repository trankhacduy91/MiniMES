using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MiniMes.Application.Common;
using MiniMes.Application.Planning;
using MiniMes.Application.Realtime;
using MiniMes.Contracts.Andon;
using MiniMes.Domain.Andon;
using MiniMes.Domain.Common;

namespace MiniMes.Application.Andon;

// ---------- [v2] Escalation (spec M4 2.9b, step 7b): sent by the AndonWatcherService every 5 s ----------

// Returns how many calls went up a level. Level 0 is the raise itself (pushed to the line by RaiseAndon); only
// levels >= 1 are pushed to the role groups of the rule.
public sealed record EscalateAndonsCommand : IRequest<int>;

public sealed class EscalateAndonsHandler(IAppDbContext db, TimeProvider time, IRealtimeNotifier notifier, ILogger<EscalateAndonsHandler> log)
    : IRequestHandler<EscalateAndonsCommand, int>
{
    // Seed level 2: "the production manager" in real life (e-mail / Teams / Zalo); here an Error in the log.
    public const int ErrorLogLevel = 2;

    public async Task<int> Handle(EscalateAndonsCommand cmd, CancellationToken ct)
    {
        var raised = await db.AndonCalls.Where(a => a.Status == AndonStatus.Raised).ToListAsync(ct);
        if (raised.Count == 0) return 0;
        var rules = await db.AndonEscalationRules.AsNoTracking().ToListAsync(ct);
        var now = time.GetUtcNow().UtcDateTime;

        var escalated = new List<AndonCall>();
        foreach (var call in raised)
        {
            if (!call.Escalate(AndonEscalation.DueLevel(rules, call.Type, now - call.RaisedAt), now)) continue;
            // One save per call: a supervisor acknowledging this call right now (RowVersion -> 409) must not stop
            // the others. The loser here is the watcher: it drops this call; the next tick sees Acknowledged.
            try
            {
                await db.SaveChangesAsync(ct);
                escalated.Add(call);
            }
            catch (ConflictException)
            {
                db.Entry(call).State = EntityState.Detached;
                log.LogInformation("Andon {AndonId} changed while escalating (acknowledged?); skipped", call.Id);
            }
        }

        foreach (var call in escalated)
        {
            var roles = rules.Where(r => r.AndonType == call.Type && r.Level == call.EscalationLevel).SelectMany(r => r.Roles).ToList();
            var dto = await db.GetAndonDtoAsync(call.Id, ct);
            log.Log(call.EscalationLevel >= ErrorLogLevel ? LogLevel.Error : LogLevel.Warning,
                "Andon {AndonId} on {Machine} ({Type}) not acknowledged after {Minutes:F0} min: escalated to level {Level}, notifying {Roles}",
                dto.Id, dto.Machine, dto.Type, (now - call.RaisedAt).TotalMinutes, call.EscalationLevel, string.Join(",", roles));
            await notifier.AndonEscalatedAsync(dto, roles, ct);
            await notifier.AndonChangedAsync(dto, ct);   // the dashboard shows the level (step 8)
        }
        return escalated.Count;
    }
}

// ---------- [v2] Statistics (Supervisor, Maintenance, Admin) ----------

public sealed record GetAndonStatsQuery(DateTime From, DateTime To) : IRequest<AndonStatsDto>;

public sealed class GetAndonStatsValidator : AbstractValidator<GetAndonStatsQuery>
{
    public const int MaxDays = 366;

    public GetAndonStatsValidator()
    {
        RuleFor(x => x.From).NotEmpty();
        RuleFor(x => x.To).NotEmpty().GreaterThan(x => x.From).WithMessage("'To' must be after 'From'.");
        RuleFor(x => x).Must(x => x.To - x.From <= TimeSpan.FromDays(MaxDays))
            .WithName("To").WithMessage($"The range is at most {MaxDays} days.");
    }
}

// A month has hundreds of calls, not millions: three columns are read and counted in memory, which keeps the
// arithmetic plain C# instead of provider-specific DATEDIFF.
public sealed class GetAndonStatsHandler(IAppDbContext db) : IRequestHandler<GetAndonStatsQuery, AndonStatsDto>
{
    public async Task<AndonStatsDto> Handle(GetAndonStatsQuery q, CancellationToken ct)
    {
        var (from, to) = (WorkOrderRules.AsUtc(q.From), WorkOrderRules.AsUtc(q.To));
        var calls = await db.AndonCalls.AsNoTracking()
            .Where(a => a.Status != AndonStatus.Cancelled && a.RaisedAt >= from && a.RaisedAt < to)
            .Select(a => new { a.EscalationLevel, a.RaisedAt, a.AckAt })
            .ToListAsync(ct);

        var byLevel = calls.GroupBy(c => c.EscalationLevel).OrderBy(g => g.Key)
            .Select(g => new AndonLevelCountDto(g.Key, g.Count())).ToList();
        var reactions = calls.Where(c => c.AckAt is not null).Select(c => (c.AckAt!.Value - c.RaisedAt).TotalSeconds).ToList();
        return new AndonStatsDto(from, to, calls.Count, byLevel, reactions.Count,
            reactions.Count == 0 ? null : Math.Round(reactions.Average(), 1));
    }
}
