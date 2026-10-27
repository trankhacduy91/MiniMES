using FluentValidation;
using FluentValidation.Results;
using MediatR;
using Microsoft.EntityFrameworkCore;
using MiniMes.Application.Common;
using MiniMes.Application.Realtime;
using MiniMes.Contracts.Andon;
using MiniMes.Domain.Andon;
using MiniMes.Domain.Common;

namespace MiniMes.Application.Andon;

// Spec M4 2.9 / step 7. Every change: save first, then push AndonChanged to the line (at once, not throttled).
internal static class AndonQueries
{
    // Machine and line codes ride along so the dashboard and the SignalR group need no second lookup.
    // Filter and order go before the projection: EF cannot translate a Where on a record built by its constructor.
    public static IQueryable<AndonDto> SelectDtos(this IAppDbContext db, IQueryable<AndonCall> calls, string? line = null)
        => from a in calls
           join m in db.Machines on a.MachineId equals m.Id
           join wc in db.WorkCenters on m.WorkCenterId equals wc.Id
           join l in db.Lines on wc.LineId equals l.Id
           where line == null || l.Code == line
           join r in db.DowntimeReasons on a.DowntimeReasonId equals r.Id into reasons
           from r in reasons.DefaultIfEmpty()
           orderby a.RaisedAt descending, a.Id descending
           select new AndonDto(a.Id, a.MachineId, m.Code, l.Code, a.Type.ToString(), a.Status.ToString(),
               a.RaisedBy, a.RaisedAt, a.AckBy, a.AckAt, a.ResolvedBy, a.ResolvedAt,
               a.DowntimeReasonId, r == null ? null : r.Code, a.ResolutionNote,
               a.EscalationLevel, Convert.ToBase64String(a.RowVersion));

    public static async Task<AndonDto> GetAndonDtoAsync(this IAppDbContext db, long id, CancellationToken ct)
        => await db.SelectDtos(db.AndonCalls.AsNoTracking().Where(a => a.Id == id)).SingleOrDefaultAsync(ct)
           ?? throw new KeyNotFoundException($"Andon {id} not found.");

    public static Task<AndonCall?> FindOpenAsync(this IAppDbContext db, int machineId, AndonType type, CancellationToken ct)
        => db.AndonCalls.AsNoTracking()
            .Where(a => a.MachineId == machineId && a.Type == type && a.Status <= AndonStatus.Acknowledged)
            .SingleOrDefaultAsync(ct);
}

// ---------- Raise (Operator; the AndonWatcherService with Auto = true) ----------

// Auto is never bound from HTTP: the controller always sends false, so only the server can write "system".
public sealed record RaiseAndonCommand(int MachineId, string Type, bool Auto = false) : IRequest<RaiseAndonResult>;

public sealed class RaiseAndonValidator : AbstractValidator<RaiseAndonCommand>
{
    public RaiseAndonValidator()
    {
        RuleFor(x => x.MachineId).GreaterThan(0);
        RuleFor(x => x.Type).Must(t => AndonTypes.All.Contains(t))
            .WithMessage($"Type must be one of: {string.Join(", ", AndonTypes.All)}.");
    }
}

// At most one open call per (machine, type). The check below answers the normal case (the operator presses the
// button after the auto-raise); the filtered unique index answers the race (both at the same moment): the loser's
// INSERT fails (409 inside) and it returns the winner's call instead of an error.
public sealed class RaiseAndonHandler(IAppDbContext db, ICurrentUser user, TimeProvider time, IRealtimeNotifier notifier)
    : IRequestHandler<RaiseAndonCommand, RaiseAndonResult>
{
    public async Task<RaiseAndonResult> Handle(RaiseAndonCommand cmd, CancellationToken ct)
    {
        var type = Enum.Parse<AndonType>(cmd.Type);
        var machine = await db.Machines.AsNoTracking().SingleOrDefaultAsync(m => m.Id == cmd.MachineId, ct)
            ?? throw new KeyNotFoundException($"Machine {cmd.MachineId} not found.");
        if (!machine.IsActive) throw new DomainException($"Machine '{machine.Code}' is inactive.");

        if (await db.FindOpenAsync(machine.Id, type, ct) is { } open)
            return new RaiseAndonResult(await db.GetAndonDtoAsync(open.Id, ct), Created: false);

        var raisedBy = cmd.Auto ? AndonCall.SystemUser
            : user.EmployeeCode ?? throw new InvalidOperationException("Raising an Andon needs a logged-in user.");
        var call = new AndonCall(machine.Id, type, raisedBy, time.GetUtcNow().UtcDateTime);
        db.AndonCalls.Add(call);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (ConflictException)
        {
            db.ChangeTracker.Clear();
            var winner = await db.FindOpenAsync(machine.Id, type, ct);
            if (winner is null) throw;   // not the open-call index after all
            return new RaiseAndonResult(await db.GetAndonDtoAsync(winner.Id, ct), Created: false);
        }

        var dto = await db.GetAndonDtoAsync(call.Id, ct);
        await notifier.AndonChangedAsync(dto, ct);
        return new RaiseAndonResult(dto, Created: true);
    }
}

// ---------- Acknowledge / Resolve / Cancel (Supervisor, Maintenance, Admin) ----------

public interface IAndonTransition
{
    long Id { get; }
    string RowVersion { get; }
}

public abstract class AndonTransitionValidator<T> : AbstractValidator<T> where T : IAndonTransition
{
    protected AndonTransitionValidator()
        => RuleFor(x => x.RowVersion).NotEmpty()
            .Must(v => v is not null && Convert.TryFromBase64String(v, new byte[v.Length], out _))
            .WithMessage("RowVersion must be the base64 value returned by the API.");
}

// The RowVersion is the version the user saw on screen. Two supervisors pressing Acknowledge on the same call:
//  - the second one reads after the first committed -> versions differ -> 409 here, before the Domain says
//    "already acknowledged" (422): for the user it is "someone else took it", not "you did something illegal";
//  - both read before either saved -> both pass this check, the second UPDATE matches 0 rows -> 409 (AppDbContext).
internal sealed class AndonTransition(IAppDbContext db, ICurrentUser user, TimeProvider time, IRealtimeNotifier notifier)
{
    public async Task<AndonDto> RunAsync(IAndonTransition cmd, Action<AndonCall, string, DateTime> change, CancellationToken ct)
    {
        var call = await db.AndonCalls.SingleOrDefaultAsync(a => a.Id == cmd.Id, ct)
            ?? throw new KeyNotFoundException($"Andon {cmd.Id} not found.");
        var seen = Convert.FromBase64String(cmd.RowVersion);
        if (!call.RowVersion.AsSpan().SequenceEqual(seen))
            throw new ConflictException($"Andon {call.Id} was changed by someone else (now {call.Status}). Reload and try again.");
        db.Entry(call).Property(a => a.RowVersion).OriginalValue = seen;

        var by = user.EmployeeCode ?? throw new InvalidOperationException("Changing an Andon needs a logged-in user.");
        change(call, by, time.GetUtcNow().UtcDateTime);   // wrong status -> 422
        await db.SaveChangesAsync(ct);

        var dto = await db.GetAndonDtoAsync(call.Id, ct);
        await notifier.AndonChangedAsync(dto, ct);
        return dto;
    }
}

public sealed record AcknowledgeAndonCommand(long Id, string RowVersion) : IRequest<AndonDto>, IAndonTransition;

public sealed class AcknowledgeAndonValidator : AndonTransitionValidator<AcknowledgeAndonCommand>;

public sealed class AcknowledgeAndonHandler(IAppDbContext db, ICurrentUser user, TimeProvider time, IRealtimeNotifier notifier)
    : IRequestHandler<AcknowledgeAndonCommand, AndonDto>
{
    public Task<AndonDto> Handle(AcknowledgeAndonCommand cmd, CancellationToken ct)
        => new AndonTransition(db, user, time, notifier).RunAsync(cmd, (a, by, now) => a.Acknowledge(by, now), ct);
}

// The downtime reason is what M5 builds the stop Pareto from: required (400 when missing or unknown).
public sealed record ResolveAndonCommand(long Id, int? DowntimeReasonId, string? Note, string RowVersion) : IRequest<AndonDto>, IAndonTransition;

public sealed class ResolveAndonValidator : AndonTransitionValidator<ResolveAndonCommand>
{
    public ResolveAndonValidator()
    {
        RuleFor(x => x.DowntimeReasonId).NotNull().GreaterThan(0);
        RuleFor(x => x.Note).MaximumLength(AndonCall.NoteMaxLength);
    }
}

public sealed class ResolveAndonHandler(IAppDbContext db, ICurrentUser user, TimeProvider time, IRealtimeNotifier notifier)
    : IRequestHandler<ResolveAndonCommand, AndonDto>
{
    public async Task<AndonDto> Handle(ResolveAndonCommand cmd, CancellationToken ct)
    {
        var reasonId = cmd.DowntimeReasonId!.Value;
        if (!await db.DowntimeReasons.AnyAsync(r => r.Id == reasonId && r.IsActive, ct))
            throw new ValidationException([new ValidationFailure(nameof(cmd.DowntimeReasonId), $"Unknown or inactive downtime reason {reasonId}.")]);
        return await new AndonTransition(db, user, time, notifier).RunAsync(cmd, (a, by, now) => a.Resolve(by, reasonId, cmd.Note, now), ct);
    }
}

public sealed record CancelAndonCommand(long Id, string RowVersion) : IRequest<AndonDto>, IAndonTransition;

public sealed class CancelAndonValidator : AndonTransitionValidator<CancelAndonCommand>;

public sealed class CancelAndonHandler(IAppDbContext db, ICurrentUser user, TimeProvider time, IRealtimeNotifier notifier)
    : IRequestHandler<CancelAndonCommand, AndonDto>
{
    public Task<AndonDto> Handle(CancelAndonCommand cmd, CancellationToken ct)
        => new AndonTransition(db, user, time, notifier).RunAsync(cmd, (a, by, now) => a.Cancel(by, now), ct);
}

// ---------- Queries (every logged-in role) ----------

// status: "open" (Raised + Acknowledged, what the dashboard shows) or one status name; line: a line code.
// Newest first, capped: closed calls pile up, reports on them belong to M5.
public sealed record GetAndonCallsQuery(string? Status, string? Line) : IRequest<IReadOnlyList<AndonDto>>;

public sealed class GetAndonCallsValidator : AbstractValidator<GetAndonCallsQuery>
{
    public GetAndonCallsValidator()
        => RuleFor(x => x.Status)
            .Must(s => s is null || s == GetAndonCallsHandler.Open || Enum.TryParse<AndonStatus>(s, out _) && !int.TryParse(s, out _))
            .WithMessage($"Status must be '{GetAndonCallsHandler.Open}' or one of: {string.Join(", ", Enum.GetNames<AndonStatus>())}.");
}

public sealed class GetAndonCallsHandler(IAppDbContext db) : IRequestHandler<GetAndonCallsQuery, IReadOnlyList<AndonDto>>
{
    public const string Open = "open";
    public const int MaxRows = 200;

    public async Task<IReadOnlyList<AndonDto>> Handle(GetAndonCallsQuery q, CancellationToken ct)
    {
        var calls = db.AndonCalls.AsNoTracking();
        if (q.Status == Open) calls = calls.Where(a => a.Status <= AndonStatus.Acknowledged);
        else if (q.Status is not null)
        {
            var status = Enum.Parse<AndonStatus>(q.Status);
            calls = calls.Where(a => a.Status == status);
        }

        return await db.SelectDtos(calls, q.Line).Take(MaxRows).ToListAsync(ct);
    }
}

public sealed record GetAndonByIdQuery(long Id) : IRequest<AndonDto>;

public sealed class GetAndonByIdHandler(IAppDbContext db) : IRequestHandler<GetAndonByIdQuery, AndonDto>
{
    public Task<AndonDto> Handle(GetAndonByIdQuery q, CancellationToken ct) => db.GetAndonDtoAsync(q.Id, ct);
}
