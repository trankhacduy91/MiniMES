using FluentValidation;
using FluentValidation.Results;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MiniMes.Application.Common;
using MiniMes.Contracts.Oee;
using MiniMes.Domain.Andon;
using MiniMes.Domain.Common;
using MiniMes.Domain.Machines;

namespace MiniMes.Application.Oee.Downtime;

// Spec M5 step 6. A stop without a reason still costs Availability (rule 7) and shows up here until a supervisor
// says why. Changing a reason can move the stop between "loss" and "not planned", so the snapshots of the hours and
// shift it touches are marked for recalculation (rule 8); the job recomputes them on its next minute.
internal static class DowntimeClassification
{
    public const string OtherCode = "OTHER";

    // The states that are a loss when nobody explains them (StopRules); Idle is left out: every break would land here.
    public static readonly MachineState[] ToClassify = [MachineState.Down, MachineState.Setup, MachineState.Offline];

    public static async Task MarkSnapshotsForRecalculationAsync(IAppDbContext db, MachineStateHistory segment, CancellationToken ct)
    {
        var start = segment.StartAt;
        var end = segment.EndAt ?? DateTime.MaxValue;
        var rows = await db.OeeSnapshots
            .Where(s => s.MachineId == segment.MachineId && s.IsFinal && s.WindowStartUtc < end && s.WindowEndUtc > start)
            .ToListAsync(ct);
        foreach (var row in rows) row.MarkForRecalculation();
    }

    // Spec 1.3 step 4: resolving an Andon with a reason explains the stops of that machine during the call, unless a
    // supervisor already classified them (an explicit choice wins over the Andon's).
    public static async Task ClassifyFromAndonAsync(IAppDbContext db, AndonCall call, CancellationToken ct)
    {
        if (call.DowntimeReasonId is not { } reasonId || call.ResolvedAt is not { } resolvedAt) return;
        var segments = await db.MachineStateHistory
            .Where(h => h.MachineId == call.MachineId && h.DowntimeReasonId == null && h.State != MachineState.Running
                        && h.StartAt < resolvedAt && (h.EndAt == null || h.EndAt > call.RaisedAt))
            .ToListAsync(ct);
        foreach (var segment in segments)
        {
            segment.AssignReason(reasonId, call.ResolutionNote);
            await MarkSnapshotsForRecalculationAsync(db, segment, ct);
        }
    }
}

// ---------- Query: what is left to classify ----------

/// <param name="From">UTC; default: 7 days before now.</param>
public sealed record GetUnclassifiedDowntimeQuery(int? MachineId, DateTime? From, DateTime? To)
    : IRequest<IReadOnlyList<UnclassifiedDowntimeDto>>;

public sealed class GetUnclassifiedDowntimeHandler(IAppDbContext db, TimeProvider time, IOptions<OeeOptions> options)
    : IRequestHandler<GetUnclassifiedDowntimeQuery, IReadOnlyList<UnclassifiedDowntimeDto>>
{
    public const int MaxRows = 500;

    public async Task<IReadOnlyList<UnclassifiedDowntimeDto>> Handle(GetUnclassifiedDowntimeQuery q, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var start = q.From ?? now.AddDays(-7);
        var end = q.To ?? now;
        // Micro stops never need a reason (rule 6): same threshold as the OEE maths. Open segments are left out:
        // the stop is not over, its length and cause are not known yet.
        var minSeconds = options.Value.MicroStopThresholdSec;
        var rows = await (
                from h in db.MachineStateHistory
                join m in db.Machines on h.MachineId equals m.Id
                join w in db.WorkCenters on m.WorkCenterId equals w.Id
                join l in db.Lines on w.LineId equals l.Id
                where h.DowntimeReasonId == null && DowntimeClassification.ToClassify.Contains(h.State)
                      && h.EndAt != null && h.StartAt < end && h.EndAt > start
                      && (q.MachineId == null || h.MachineId == q.MachineId)
                      && h.EndAt >= h.StartAt.AddSeconds(minSeconds)
                orderby h.StartAt descending
                select new { h.Id, h.MachineId, Machine = m.Code, Line = l.Code, h.State, h.StartAt, EndAt = h.EndAt!.Value })
            .Take(MaxRows)
            .ToListAsync(ct);
        return rows.Select(r => new UnclassifiedDowntimeDto(r.Id, r.MachineId, r.Machine, r.Line, r.State.ToString(),
            r.StartAt, r.EndAt, Math.Round((r.EndAt - r.StartAt).TotalMinutes, 1))).ToList();
    }
}

// ---------- Command: give a stop its reason (Supervisor) ----------

public sealed record AssignDowntimeReasonCommand(long SegmentId, int DowntimeReasonId, string? Note) : IRequest<DowntimeSegmentDto>;

public sealed class AssignDowntimeReasonValidator : AbstractValidator<AssignDowntimeReasonCommand>
{
    public AssignDowntimeReasonValidator()
    {
        RuleFor(x => x.DowntimeReasonId).GreaterThan(0);
        RuleFor(x => x.Note).MaximumLength(MachineStateHistory.NoteMaxLength);
    }
}

public sealed class AssignDowntimeReasonHandler(IAppDbContext db) : IRequestHandler<AssignDowntimeReasonCommand, DowntimeSegmentDto>
{
    public async Task<DowntimeSegmentDto> Handle(AssignDowntimeReasonCommand cmd, CancellationToken ct)
    {
        var reason = await db.DowntimeReasons.SingleOrDefaultAsync(r => r.Id == cmd.DowntimeReasonId && r.IsActive, ct)
            ?? throw new ValidationException([new ValidationFailure(nameof(cmd.DowntimeReasonId), $"Unknown or inactive downtime reason {cmd.DowntimeReasonId}.")]);
        if (reason.Code == DowntimeClassification.OtherCode && string.IsNullOrWhiteSpace(cmd.Note))
            throw new ValidationException([new ValidationFailure(nameof(cmd.Note), "The reason OTHER needs a note saying what happened.")]);
        var segment = await db.MachineStateHistory.SingleOrDefaultAsync(h => h.Id == cmd.SegmentId, ct)
            ?? throw new KeyNotFoundException($"Downtime segment {cmd.SegmentId} not found.");
        if (segment.IsOpen)
            throw new DomainException("The machine is still in this state; classify the stop once it is over.");

        segment.AssignReason(reason.Id, cmd.Note);   // Running -> 422; the change is audited column by column
        await DowntimeClassification.MarkSnapshotsForRecalculationAsync(db, segment, ct);
        await db.SaveChangesAsync(ct);

        var machine = await db.Machines.Where(m => m.Id == segment.MachineId).Select(m => m.Code).SingleAsync(ct);
        return new DowntimeSegmentDto(segment.Id, segment.MachineId, machine, segment.State.ToString(), segment.StartAt,
            segment.EndAt, reason.Id, reason.Code, segment.DowntimeNote);
    }
}
