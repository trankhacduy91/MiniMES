using FluentValidation;
using FluentValidation.Results;
using MediatR;
using Microsoft.EntityFrameworkCore;
using MiniMes.Application.Common;
using MiniMes.Application.Planning;
using MiniMes.Application.Realtime;
using MiniMes.Contracts.Quality;
using MiniMes.Domain.Common;
using MiniMes.Domain.MasterData;
using MiniMes.Domain.Planning;
using MiniMes.Domain.Quality.Fai;

namespace MiniMes.Application.Quality.Fai;

// ---------- Queue: WOs whose first article waits for QC (QC, Supervisor, Admin) ----------

public sealed record GetFaiQueueQuery : IRequest<IReadOnlyList<FaiQueueItemDto>>;

public sealed class GetFaiQueueHandler(IAppDbContext db) : IRequestHandler<GetFaiQueueQuery, IReadOnlyList<FaiQueueItemDto>>
{
    private static readonly WorkOrderStatus[] OnTheLine = [WorkOrderStatus.Released, WorkOrderStatus.InProgress, WorkOrderStatus.OnHold];

    public async Task<IReadOnlyList<FaiQueueItemDto>> Handle(GetFaiQueueQuery q, CancellationToken ct)
    {
        var rows = await (from w in db.WorkOrders.AsNoTracking()
                          join i in db.Items on w.ItemId equals i.Id
                          join l in db.Lines on w.LineId equals l.Id
                          join s in db.Serials on w.FaiSerialNo equals s.SerialNo into sample
                          from s in sample.DefaultIfEmpty()
                          where w.FaiStatus == FaiStatus.Pending && OnTheLine.Contains(w.Status)
                          select new
                          {
                              w.Id, w.WoNo, Item = i.Code, Line = l.Code, w.Status, w.FaiSerialNo,
                              SampleStatus = (SerialStatus?)s.Status, SampleSeq = (int?)s.CurrentSeq,
                              // Q17: the last inspection step (or the last step) the sample must have passed
                              Gate = w.Operations.Where(o => o.IsInspection).Max(o => (int?)o.Seq) ?? w.Operations.Max(o => o.Seq),
                          }).ToListAsync(ct);
        return rows.Select(r => new FaiQueueItemDto(r.Id, r.WoNo, r.Item, r.Line, r.Status.ToString(), r.FaiSerialNo,
                r.SampleStatus?.ToString(),
                r.SampleStatus is SerialStatus.InProcess or SerialStatus.Completed && r.SampleSeq >= r.Gate))
            .OrderByDescending(r => r.SampleReady).ThenBy(r => r.WoNo).ToList();
    }
}

// ---------- Worksheet of one WO (QC, Supervisor, Admin) ----------

public sealed record GetFaiWorksheetQuery(int WorkOrderId) : IRequest<FaiWorksheetDto>;

public sealed class GetFaiWorksheetHandler(IAppDbContext db) : IRequestHandler<GetFaiWorksheetQuery, FaiWorksheetDto>
{
    public async Task<FaiWorksheetDto> Handle(GetFaiWorksheetQuery q, CancellationToken ct)
    {
        var wo = await db.WorkOrders.AsNoTracking().Include(w => w.Operations).SingleOrDefaultAsync(w => w.Id == q.WorkOrderId, ct)
                 ?? throw new KeyNotFoundException($"Work order {q.WorkOrderId} not found.");
        return await db.ToDtoAsync(wo, await db.ReadAsync(wo, null, ct), ct);
    }
}

// ---------- Record the result (QC, Admin), spec 2.5b "luồng ghi" ----------

public sealed record RecordFaiResultCommand(int WorkOrderId, string Result, IReadOnlyList<string> CheckedItems, string? Note,
    string? DefectCode, string RowVersion) : IRequest<FaiWorksheetDto>;

public sealed class RecordFaiResultValidator : AbstractValidator<RecordFaiResultCommand>
{
    public RecordFaiResultValidator()
    {
        RuleFor(x => x.Result).Must(r => Enum.TryParse<FaiResult>(r, ignoreCase: true, out _)).WithMessage("Result must be Pass or Fail.");
        RuleFor(x => x.CheckedItems).Must(c => c.Count <= 50).WithMessage("At most 50 checked items.");
        RuleFor(x => x.Note).MaximumLength(FaiRecord.NoteMaxLength);
        RuleFor(x => x.DefectCode).MaximumLength(MasterDataRules.CodeMaxLength);
        RuleFor(x => x.RowVersion).RowVersionRule();
    }
}

// One SaveChanges = one transaction: the FaiRecord, the WO's FaiStatus/FaiSerialNo (rowversion: two QCs at once ->
// one 409) and, on Fail, the sample going NG. The route check (M3) reads the WO from the database on every scan,
// so there is no cache to clear: the next scan sees Passed. Then the line's kiosks are told (banner only).
public sealed class RecordFaiResultHandler(IAppDbContext db, ICurrentUser user, TimeProvider time, IRealtimeNotifier notifier)
    : IRequestHandler<RecordFaiResultCommand, FaiWorksheetDto>
{
    public async Task<FaiWorksheetDto> Handle(RecordFaiResultCommand cmd, CancellationToken ct)
    {
        var wo = await db.WorkOrders.Include(w => w.Operations).SingleOrDefaultAsync(w => w.Id == cmd.WorkOrderId, ct)
                 ?? throw new KeyNotFoundException($"Work order {cmd.WorkOrderId} not found.");
        var seen = Convert.FromBase64String(cmd.RowVersion);
        if (!wo.RowVersion.AsSpan().SequenceEqual(seen))
            throw new ConflictException($"Work order {wo.WoNo} changed since the worksheet was opened (FAI now {wo.FaiStatus}). Reload it.");
        db.Entry(wo).Property(w => w.RowVersion).OriginalValue = seen;

        var result = Enum.Parse<FaiResult>(cmd.Result, ignoreCase: true);
        var data = await db.ReadAsync(wo, cmd.CheckedItems, ct);
        if (data.NotReadyReason is { } why) throw new DomainException(why);   // Q17: no sample, not through Test...
        var manual = data.Template!.Items().Where(i => i.Kind == FaiCheckKind.Manual).Select(i => i.Code).ToList();
        var unknown = cmd.CheckedItems.Where(c => !manual.Contains(c, StringComparer.OrdinalIgnoreCase)).ToList();
        if (unknown.Count > 0)
            throw new ValidationException([new ValidationFailure(nameof(cmd.CheckedItems),
                $"Not a manual item of the checklist: {string.Join(", ", unknown)}.")]);
        data.Sheet.RequireCanRecord(result);   // Q17: Pass needs green automatic checks and every manual item -> 422

        string? defect = null;
        if (result == FaiResult.Fail)
        {
            defect = string.IsNullOrWhiteSpace(cmd.DefectCode) ? FaiWorksheet.DefaultDefectCode : cmd.DefectCode.Trim().ToUpperInvariant();
            if (!await db.DefectCodes.AnyAsync(d => d.Code == defect && d.IsActive, ct))
                throw new ValidationException([new ValidationFailure(nameof(cmd.DefectCode), $"Unknown or inactive defect code '{defect}'.")]);
        }

        var by = user.EmployeeCode ?? throw new InvalidOperationException("Recording a FAI needs a logged-in user.");
        var sample = data.Sample!;
        db.FaiRecords.Add(new FaiRecord(wo.Id, sample.Id, data.Template.Id, data.Sheet.ToJson(), data.Sheet.AutoChecksPassed,
            result, cmd.Note, by, time.GetUtcNow().UtcDateTime, defect));
        if (result == FaiResult.Fail) sample.RejectFirstArticle();   // packed already -> 422, before anything is written
        wo.RecordFaiResult(result == FaiResult.Pass);                 // Fail: FaiSerialNo cleared, the next board is the sample
        await db.SaveChangesAsync(ct);

        await notifier.FaiStatusChangedAsync(new FaiStatusChangedDto(wo.Id, wo.WoNo, data.Line, wo.FaiStatus!.Value.ToString(),
            wo.FaiSerialNo, result.ToString()), ct);
        return await db.ToDtoAsync(wo, await db.ReadAsync(wo, null, ct), ct);
    }
}
