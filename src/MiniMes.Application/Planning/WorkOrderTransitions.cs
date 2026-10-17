using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using MiniMes.Application.Common;
using MiniMes.Contracts.Planning;
using MiniMes.Domain.Planning;

namespace MiniMes.Application.Planning;

// The remaining status changes (spec M2 step 7). Each one: load the WO with its operations (HasOutput needs
// them), take the RowVersion the user saw, let the Domain method decide (wrong status -> 422), save (stale
// RowVersion -> 409). The audit interceptor records the Status change with who and when.
internal static class Transition
{
    public static async Task<WorkOrderDetailDto> RunAsync(IAppDbContext db, int id, string rowVersion,
        Action<WorkOrder> change, CancellationToken ct)
    {
        var wo = await db.LoadWorkOrderAsync(id, ct);
        db.ApplyRowVersion(wo, rowVersion);
        change(wo);
        await db.SaveChangesAsync(ct);
        return await db.GetWorkOrderDtoAsync(wo.Id, ct);
    }
}

public interface IWorkOrderTransition
{
    string RowVersion { get; }
}

public abstract class TransitionValidator<T> : AbstractValidator<T> where T : IWorkOrderTransition
{
    protected TransitionValidator() => RuleFor(x => x.RowVersion).RowVersionRule();
}

// ---------- Hold / Resume (Supervisor) ----------

public sealed record HoldWorkOrderCommand(int Id, string RowVersion, string? Reason) : IRequest<WorkOrderDetailDto>, IWorkOrderTransition;

public sealed class HoldWorkOrderValidator : TransitionValidator<HoldWorkOrderCommand>
{
    public HoldWorkOrderValidator() => RuleFor(x => x.Reason).NotEmpty().MaximumLength(WorkOrder.ReasonMaxLength);
}

public sealed class HoldWorkOrderHandler(IAppDbContext db) : IRequestHandler<HoldWorkOrderCommand, WorkOrderDetailDto>
{
    public Task<WorkOrderDetailDto> Handle(HoldWorkOrderCommand cmd, CancellationToken ct)
        => Transition.RunAsync(db, cmd.Id, cmd.RowVersion, wo => wo.Hold(cmd.Reason!), ct);
}

public sealed record ResumeWorkOrderCommand(int Id, string RowVersion) : IRequest<WorkOrderDetailDto>, IWorkOrderTransition;

public sealed class ResumeWorkOrderValidator : TransitionValidator<ResumeWorkOrderCommand>;

public sealed class ResumeWorkOrderHandler(IAppDbContext db) : IRequestHandler<ResumeWorkOrderCommand, WorkOrderDetailDto>
{
    public Task<WorkOrderDetailDto> Handle(ResumeWorkOrderCommand cmd, CancellationToken ct)
        => Transition.RunAsync(db, cmd.Id, cmd.RowVersion, wo => wo.Resume(), ct);
}

// ---------- Complete / Close (Supervisor) ----------

// Reason only matters below the planned quantity (rule R10); the Domain asks for it then.
public sealed record CompleteWorkOrderCommand(int Id, string RowVersion, string? Reason) : IRequest<WorkOrderDetailDto>, IWorkOrderTransition;

public sealed class CompleteWorkOrderValidator : TransitionValidator<CompleteWorkOrderCommand>
{
    public CompleteWorkOrderValidator() => RuleFor(x => x.Reason).MaximumLength(WorkOrder.ReasonMaxLength);
}

public sealed class CompleteWorkOrderHandler(IAppDbContext db, TimeProvider time) : IRequestHandler<CompleteWorkOrderCommand, WorkOrderDetailDto>
{
    public Task<WorkOrderDetailDto> Handle(CompleteWorkOrderCommand cmd, CancellationToken ct)
        => Transition.RunAsync(db, cmd.Id, cmd.RowVersion, wo => wo.Complete(cmd.Reason, time.GetUtcNow().UtcDateTime), ct);
}

public sealed record CloseWorkOrderCommand(int Id, string RowVersion) : IRequest<WorkOrderDetailDto>, IWorkOrderTransition;

public sealed class CloseWorkOrderValidator : TransitionValidator<CloseWorkOrderCommand>;

public sealed class CloseWorkOrderHandler(IAppDbContext db) : IRequestHandler<CloseWorkOrderCommand, WorkOrderDetailDto>
{
    public Task<WorkOrderDetailDto> Handle(CloseWorkOrderCommand cmd, CancellationToken ct)
        => Transition.RunAsync(db, cmd.Id, cmd.RowVersion, wo => wo.Close(), ct);
}

// ---------- Cancel (Planner) ----------

public sealed record CancelWorkOrderCommand(int Id, string RowVersion, string? Reason) : IRequest<WorkOrderDetailDto>, IWorkOrderTransition;

public sealed class CancelWorkOrderValidator : TransitionValidator<CancelWorkOrderCommand>
{
    public CancelWorkOrderValidator() => RuleFor(x => x.Reason).NotEmpty().MaximumLength(WorkOrder.ReasonMaxLength);
}

// Rule R8: the unused serials of a cancelled WO become Scrapped (kept as a trace, never reused). One set-based
// UPDATE instead of loading thousands of serials; it runs in the same explicit transaction as the WO change,
// so a 409 on the WO also leaves every serial untouched.
public sealed class CancelWorkOrderHandler(IAppDbContext db) : IRequestHandler<CancelWorkOrderCommand, WorkOrderDetailDto>
{
    public async Task<WorkOrderDetailDto> Handle(CancelWorkOrderCommand cmd, CancellationToken ct)
    {
        await using var tx = await db.BeginTransactionAsync(ct);
        var wo = await db.LoadWorkOrderAsync(cmd.Id, ct);
        db.ApplyRowVersion(wo, cmd.RowVersion);
        wo.Cancel(cmd.Reason!);   // R7: refused once there is output
        await db.SaveChangesAsync(ct);

        var scrapReason = $"Work order cancelled: {cmd.Reason}";
        if (scrapReason.Length > Serial.ScrapReasonMaxLength) scrapReason = scrapReason[..Serial.ScrapReasonMaxLength];
        await db.Serials.Where(s => s.WorkOrderId == wo.Id && s.Status == SerialStatus.Created)
            .ExecuteUpdateAsync(set => set
                .SetProperty(s => s.Status, SerialStatus.Scrapped)
                .SetProperty(s => s.ScrapReason, scrapReason), ct);

        await tx.CommitAsync(ct);
        return await db.GetWorkOrderDtoAsync(wo.Id, ct);
    }
}

// ---------- [v2] Priority (Planner, Supervisor) ----------

public sealed record ChangeWorkOrderPriorityCommand(int Id, int Priority, string RowVersion) : IRequest<WorkOrderDetailDto>, IWorkOrderTransition;

public sealed class ChangeWorkOrderPriorityValidator : TransitionValidator<ChangeWorkOrderPriorityCommand>
{
    public ChangeWorkOrderPriorityValidator()
        => RuleFor(x => x.Priority).InclusiveBetween(WorkOrder.MinPriority, WorkOrder.MaxPriority);
}

public sealed class ChangeWorkOrderPriorityHandler(IAppDbContext db) : IRequestHandler<ChangeWorkOrderPriorityCommand, WorkOrderDetailDto>
{
    public Task<WorkOrderDetailDto> Handle(ChangeWorkOrderPriorityCommand cmd, CancellationToken ct)
        => Transition.RunAsync(db, cmd.Id, cmd.RowVersion, wo => wo.ChangePriority(cmd.Priority), ct);
}

// ---------- [v2] FAI result (QC), temporary until M7 ----------

// M3 step 3b: lets QC open the line (passed) or ask for a new sample (failed) before M7 brings the FAI checklist.
// No RowVersion from the client: QC judges the sample, not a version of the form; a concurrent change of the WO
// still fails on the loaded RowVersion (409).
public sealed record RecordFaiResultCommand(int Id, bool Passed) : IRequest<WorkOrderDetailDto>;

public sealed class RecordFaiResultHandler(IAppDbContext db) : IRequestHandler<RecordFaiResultCommand, WorkOrderDetailDto>
{
    public async Task<WorkOrderDetailDto> Handle(RecordFaiResultCommand cmd, CancellationToken ct)
    {
        var wo = await db.LoadWorkOrderAsync(cmd.Id, ct);
        wo.RecordFaiResult(cmd.Passed);   // no sample yet / not pending -> 422
        await db.SaveChangesAsync(ct);
        return await db.GetWorkOrderDtoAsync(wo.Id, ct);
    }
}

// ---------- [v2] Dispatch list of a line (every role; the kiosk in M3) ----------

public sealed record GetDispatchListQuery(int LineId) : IRequest<IReadOnlyList<DispatchListItemDto>>;

public sealed class GetDispatchListHandler(IAppDbContext db) : IRequestHandler<GetDispatchListQuery, IReadOnlyList<DispatchListItemDto>>
{
    private static readonly WorkOrderStatus[] OnTheLine = [WorkOrderStatus.Released, WorkOrderStatus.InProgress, WorkOrderStatus.OnHold];

    public async Task<IReadOnlyList<DispatchListItemDto>> Handle(GetDispatchListQuery q, CancellationToken ct)
    {
        if (!await db.Lines.AnyAsync(l => l.Id == q.LineId, ct))
            throw new KeyNotFoundException($"Line {q.LineId} not found.");

        // Index (LineId, Status, Priority, PlannedStart) serves exactly this query.
        return await (from w in db.WorkOrders.AsNoTracking()
                      join i in db.Items on w.ItemId equals i.Id
                      where w.LineId == q.LineId && OnTheLine.Contains(w.Status)
                      orderby w.Priority, w.PlannedStart, w.WoNo
                      select new DispatchListItemDto(w.Id, w.WoNo, i.Code, w.PlannedQty,
                          w.Operations.OrderByDescending(o => o.Seq).Select(o => o.QtyGood).FirstOrDefault(),
                          w.Priority, w.PlannedStart, w.Status.ToString(),
                          w.FaiStatus == null ? null : w.FaiStatus.ToString()))
            .ToListAsync(ct);
    }
}
