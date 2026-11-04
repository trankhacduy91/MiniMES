using FluentValidation;
using FluentValidation.Results;
using MediatR;
using Microsoft.EntityFrameworkCore;
using MiniMes.Application.Common;
using MiniMes.Application.Planning;
using MiniMes.Application.Quality.Holds;
using MiniMes.Contracts.Quality;
using MiniMes.Domain.Common;
using MiniMes.Domain.MasterData;
using MiniMes.Domain.Material;
using MiniMes.Domain.Planning;
using MiniMes.Domain.Quality;

namespace MiniMes.Application.Quality.Ncrs;

// ---------- Create (QC, Supervisor, Admin) ----------

public sealed record CreateNcrCommand(string Source, string? SourceRef, string? DefectCode, string Description, int Qty,
    IReadOnlyList<NcrTargetRequest> Targets, long? SpcAlertId) : IRequest<NcrDto>;

public sealed class CreateNcrValidator : AbstractValidator<CreateNcrCommand>
{
    public const int MaxTargets = 500;   // a lot used on more boards is held by lot (step 9), not listed board by board

    public CreateNcrValidator()
    {
        RuleFor(x => x.Source).Must(s => Enum.TryParse<NcrSource>(s, ignoreCase: true, out _))
            .WithMessage($"Source must be one of: {string.Join(", ", Enum.GetNames<NcrSource>())}.");
        RuleFor(x => x.SourceRef).MaximumLength(Ncr.RefMaxLength);
        RuleFor(x => x.DefectCode).MaximumLength(MasterDataRules.CodeMaxLength);
        RuleFor(x => x.Description).NotEmpty().MaximumLength(Ncr.DescriptionMaxLength);
        RuleFor(x => x.Qty).GreaterThanOrEqualTo(1);
        RuleFor(x => x.Targets).NotEmpty().Must(t => t.Count <= MaxTargets).WithMessage($"At most {MaxTargets} targets.");
        RuleForEach(x => x.Targets).ChildRules(t =>
        {
            t.RuleFor(i => i.Type).Must(v => Enum.TryParse<HoldTargetType>(v, ignoreCase: true, out _)).WithMessage("Type must be Serial or Lot.");
            t.RuleFor(i => i.Code).NotEmpty().MaximumLength(Math.Max(Serial.SerialNoMaxLength, MaterialLot.LotNoMaxLength));
        });
    }
}

public sealed class CreateNcrHandler(IAppDbContext db, ISequenceNumbers sequences) : IRequestHandler<CreateNcrCommand, NcrDto>
{
    public async Task<NcrDto> Handle(CreateNcrCommand cmd, CancellationToken ct)
    {
        var targets = await ResolveTargetsAsync(cmd.Targets, ct);
        int? defectId = null;
        if (!string.IsNullOrWhiteSpace(cmd.DefectCode))
        {
            var code = cmd.DefectCode.Trim().ToUpperInvariant();
            defectId = await db.DefectCodes.Where(d => d.Code == code && d.IsActive).Select(d => (int?)d.Id).SingleOrDefaultAsync(ct)
                       ?? throw new ValidationException([new ValidationFailure("DefectCode", $"Unknown or inactive defect code '{code}'.")]);
        }
        var source = Enum.Parse<NcrSource>(cmd.Source, ignoreCase: true);
        var sourceRef = cmd.SourceRef;
        SpcAlert? alert = null;
        if (cmd.SpcAlertId is { } alertId)
        {
            alert = await db.SpcAlerts.AsNoTracking().SingleOrDefaultAsync(a => a.Id == alertId, ct)
                    ?? throw new KeyNotFoundException($"SPC alert {alertId} not found.");
            (source, sourceRef) = (NcrSource.SpcAlert, alertId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        var ncr = new Ncr(Ncr.FormatNo(await sequences.NextNcrNumberAsync(ct)), source, sourceRef, defectId, cmd.Description,
            cmd.Qty, targets);
        await using var tx = await db.BeginTransactionAsync(ct);
        db.Ncrs.Add(ncr);
        await db.SaveChangesAsync(ct);
        // The alert gets the NCR's id, so the NCR is saved first. A conditional UPDATE, not read-then-write: two QC
        // pressing "Create NCR" on the same alert would both read "no NCR yet"; here one UPDATE finds 0 rows, its
        // transaction rolls back (its NCR number is a gap, like any rolled-back sequence value) and it gets 409.
        if (alert is not null)
        {
            if (alert.Status != SpcAlertStatus.Open) throw new DomainException($"SPC alert {alert.Id} is closed.");
            var linked = await db.SpcAlerts
                .Where(a => a.Id == alert.Id && a.NcrId == null && a.Status == SpcAlertStatus.Open)
                .ExecuteUpdateAsync(u => u.SetProperty(a => a.NcrId, ncr.Id), ct);
            if (linked == 0) throw new ConflictException($"SPC alert {alert.Id} already has an NCR. Reload and open that one.");
        }
        await tx.CommitAsync(ct);
        return await db.GetDtoAsync(ncr.Id, ct);
    }

    // Codes from labels -> Ids (step 1 decision). Unknown codes are listed together, so the user fixes them once.
    private async Task<List<(HoldTargetType, long)>> ResolveTargetsAsync(IReadOnlyList<NcrTargetRequest> requested, CancellationToken ct)
    {
        var wanted = requested.Select(t => (Type: Enum.Parse<HoldTargetType>(t.Type, ignoreCase: true), Code: t.Code.Trim().ToUpperInvariant()))
            .Distinct().ToList();
        var serialCodes = wanted.Where(w => w.Type == HoldTargetType.Serial).Select(w => w.Code).ToList();
        var lotCodes = wanted.Where(w => w.Type == HoldTargetType.Lot).Select(w => w.Code).ToList();
        var serials = await db.Serials.AsNoTracking().Where(s => serialCodes.Contains(s.SerialNo)).ToDictionaryAsync(s => s.SerialNo, s => s.Id, ct);
        var lots = await db.MaterialLots.AsNoTracking().Where(l => lotCodes.Contains(l.LotNo)).ToDictionaryAsync(l => l.LotNo, l => (long)l.Id, ct);

        var unknown = wanted.Where(w => !(w.Type == HoldTargetType.Serial ? serials : lots).ContainsKey(w.Code)).ToList();
        if (unknown.Count > 0)
            throw new ValidationException([new ValidationFailure("Targets", "Not found: " + string.Join(", ", unknown.Select(u => $"{u.Type} {u.Code}")))]);
        return wanted.Select(w => (w.Type, w.Type == HoldTargetType.Serial ? serials[w.Code] : lots[w.Code])).ToList();
    }
}

// ---------- Transitions ----------

public interface INcrTransition
{
    int Id { get; }
    string RowVersion { get; }
}

public abstract class NcrTransitionValidator<T> : AbstractValidator<T> where T : INcrTransition
{
    protected NcrTransitionValidator()
        => RuleFor(x => x.RowVersion).NotEmpty()
            .Must(v => v is not null && Convert.TryFromBase64String(v, new byte[v.Length], out _))
            .WithMessage("RowVersion must be the base64 value returned by the API.");
}

// Same shape as the Andon transitions (M4): the RowVersion the user saw, then the domain decides. Someone changed
// the NCR meanwhile -> 409 ("reload"), before the domain's 422 ("not allowed").
internal sealed class NcrTransition(IAppDbContext db, ICurrentUser user, TimeProvider time)
{
    public async Task<NcrDto> RunAsync(INcrTransition cmd, Func<Ncr, string, DateTime, Task> change, CancellationToken ct)
    {
        var ncr = await db.Ncrs.Include(n => n.Items).SingleOrDefaultAsync(n => n.Id == cmd.Id, ct)
                  ?? throw new KeyNotFoundException($"NCR {cmd.Id} not found.");
        var seen = Convert.FromBase64String(cmd.RowVersion);
        if (!ncr.RowVersion.AsSpan().SequenceEqual(seen))
            throw new ConflictException($"NCR {ncr.NcrNo} was changed by someone else (now {ncr.Status}). Reload and try again.");
        db.Entry(ncr).Property(n => n.RowVersion).OriginalValue = seen;

        var by = user.EmployeeCode ?? throw new InvalidOperationException("Changing an NCR needs a logged-in user.");
        await change(ncr, by, time.GetUtcNow().UtcDateTime);   // wrong status, self-approval... -> 422
        await db.SaveChangesAsync(ct);
        return await db.GetDtoAsync(ncr.Id, ct);
    }

    // Q12: holds placed for this NCR and still active (placed in step 9).
    public Task<int> ActiveHoldsAsync(int ncrId, CancellationToken ct)
        => db.HoldRecords.CountAsync(h => h.NcrId == ncrId && h.ReleasedAt == null, ct);

    public Task<List<SpcAlert>> AlertsAsync(int ncrId, CancellationToken ct)
        => db.SpcAlerts.Where(a => a.NcrId == ncrId).ToListAsync(ct);
}

public sealed record StartNcrReviewCommand(int Id, string RowVersion) : IRequest<NcrDto>, INcrTransition;
public sealed class StartNcrReviewValidator : NcrTransitionValidator<StartNcrReviewCommand>;

public sealed class StartNcrReviewHandler(IAppDbContext db, ICurrentUser user, TimeProvider time) : IRequestHandler<StartNcrReviewCommand, NcrDto>
{
    public Task<NcrDto> Handle(StartNcrReviewCommand cmd, CancellationToken ct)
        => new NcrTransition(db, user, time).RunAsync(cmd, (n, by, _) => { n.StartReview(by); return Task.CompletedTask; }, ct);
}

public sealed record ProposeDispositionCommand(int Id, string Disposition, string Note, string? ConcessionRef, string RowVersion)
    : IRequest<NcrDto>, INcrTransition;

public sealed class ProposeDispositionValidator : NcrTransitionValidator<ProposeDispositionCommand>
{
    public ProposeDispositionValidator()
    {
        RuleFor(x => x.Disposition).Must(d => Enum.TryParse<NcrDisposition>(d, ignoreCase: true, out _))
            .WithMessage($"Disposition must be one of: {string.Join(", ", Enum.GetNames<NcrDisposition>())}.");
        RuleFor(x => x.Note).NotEmpty().MaximumLength(Ncr.NoteMaxLength);
    }
}

public sealed class ProposeDispositionHandler(IAppDbContext db, ICurrentUser user, TimeProvider time) : IRequestHandler<ProposeDispositionCommand, NcrDto>
{
    public Task<NcrDto> Handle(ProposeDispositionCommand cmd, CancellationToken ct)
        => new NcrTransition(db, user, time).RunAsync(cmd, (n, by, now) =>
        {
            n.ProposeDisposition(Enum.Parse<NcrDisposition>(cmd.Disposition, ignoreCase: true), cmd.Note, cmd.ConcessionRef, by, now);
            return Task.CompletedTask;
        }, ct);
}

// Q11: another person than the proposer. Step 9: the decision acts on the serials / lots in one transaction with
// it (spec 2.5 effects table). Only the NCR's own targets (user decision 04/11/2026): their holds placed for this NCR
// are released (Q8: a target another hold still keeps stays Hold, the effect waits in StatusBeforeHold), then the
// disposition is applied by the Domain. Other holds of this NCR (the boards of a held lot) stay until someone releases
// them on purpose: no board is let go silently, and Q12 keeps the NCR open until then.
public sealed record ApproveNcrCommand(int Id, string RowVersion) : IRequest<NcrDto>, INcrTransition;
public sealed class ApproveNcrValidator : NcrTransitionValidator<ApproveNcrCommand>;

public sealed class ApproveNcrHandler(IAppDbContext db, ICurrentUser user, TimeProvider time) : IRequestHandler<ApproveNcrCommand, NcrDto>
{
    public async Task<NcrDto> Handle(ApproveNcrCommand cmd, CancellationToken ct)
    {
        await using var tx = await db.BeginTransactionAsync(ct);
        var dto = await new NcrTransition(db, user, time).RunAsync(cmd, async (n, by, now) =>
        {
            n.Approve(by, now);   // self-approval, no proposal -> 422, before anything changes
            var serialIds = n.Items.Where(i => i.TargetType == HoldTargetType.Serial).Select(i => i.TargetId).ToList();
            var lotIds = n.Items.Where(i => i.TargetType == HoldTargetType.Lot).Select(i => (int)i.TargetId).ToList();
            var lotLongIds = lotIds.Select(i => (long)i).ToList();
            var own = db.HoldRecords.Where(h => h.NcrId == n.Id
                && ((h.TargetType == HoldTargetType.Serial && serialIds.Contains(h.TargetId))
                    || (h.TargetType == HoldTargetType.Lot && lotLongIds.Contains(h.TargetId))));
            await db.ReleaseAsync(own, by, $"{n.NcrNo} approved: {n.Disposition}", now, $"NCR {n.NcrNo}", ct);

            // Read after the release (set-based SQL above): the entities carry the restored status.
            var disposition = n.Disposition!.Value;
            foreach (var serial in await db.Serials.Where(s => serialIds.Contains(s.Id)).ToListAsync(ct))
                serial.ApplyDisposition(disposition, n.NcrNo);
            foreach (var lot in await db.MaterialLots.Where(l => lotIds.Contains(l.Id)).ToListAsync(ct))
                lot.ApplyDisposition(disposition);
        }, ct);
        await tx.CommitAsync(ct);
        return dto;
    }
}

public sealed record RejectNcrProposalCommand(int Id, string RowVersion) : IRequest<NcrDto>, INcrTransition;
public sealed class RejectNcrProposalValidator : NcrTransitionValidator<RejectNcrProposalCommand>;

public sealed class RejectNcrProposalHandler(IAppDbContext db, ICurrentUser user, TimeProvider time) : IRequestHandler<RejectNcrProposalCommand, NcrDto>
{
    public Task<NcrDto> Handle(RejectNcrProposalCommand cmd, CancellationToken ct)
        => new NcrTransition(db, user, time).RunAsync(cmd, (n, by, _) => { n.RejectProposal(by); return Task.CompletedTask; }, ct);
}

// Q12 + spec 2.5 trap 5 (decision): the SPC alert an NCR came from closes with the NCR.
public sealed record CloseNcrCommand(int Id, string? CapaReference, string RowVersion) : IRequest<NcrDto>, INcrTransition;

public sealed class CloseNcrValidator : NcrTransitionValidator<CloseNcrCommand>
{
    public CloseNcrValidator() => RuleFor(x => x.CapaReference).MaximumLength(Ncr.RefMaxLength);
}

public sealed class CloseNcrHandler(IAppDbContext db, ICurrentUser user, TimeProvider time) : IRequestHandler<CloseNcrCommand, NcrDto>
{
    public Task<NcrDto> Handle(CloseNcrCommand cmd, CancellationToken ct)
    {
        var transition = new NcrTransition(db, user, time);
        return transition.RunAsync(cmd, async (n, by, now) =>
        {
            n.Close(by, await transition.ActiveHoldsAsync(n.Id, ct), cmd.CapaReference, now);
            foreach (var alert in (await transition.AlertsAsync(n.Id, ct)).Where(a => a.Status == SpcAlertStatus.Open))
                alert.Close(by, $"NCR {n.NcrNo} closed: {n.Disposition}.", now);
        }, ct);
    }
}

// Opened by mistake: the alert it came from is unlinked, free for a new NCR.
public sealed record CancelNcrCommand(int Id, string Reason, string RowVersion) : IRequest<NcrDto>, INcrTransition;

public sealed class CancelNcrValidator : NcrTransitionValidator<CancelNcrCommand>
{
    public CancelNcrValidator() => RuleFor(x => x.Reason).NotEmpty().MaximumLength(Ncr.NoteMaxLength);
}

public sealed class CancelNcrHandler(IAppDbContext db, ICurrentUser user, TimeProvider time) : IRequestHandler<CancelNcrCommand, NcrDto>
{
    public Task<NcrDto> Handle(CancelNcrCommand cmd, CancellationToken ct)
    {
        var transition = new NcrTransition(db, user, time);
        return transition.RunAsync(cmd, async (n, by, now) =>
        {
            n.Cancel(by, cmd.Reason, await transition.ActiveHoldsAsync(n.Id, ct), now);
            foreach (var alert in await transition.AlertsAsync(n.Id, ct)) alert.UnlinkNcr(n.Id);
        }, ct);
    }
}
