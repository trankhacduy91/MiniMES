using MediatR;
using Microsoft.EntityFrameworkCore;
using MiniMes.Application.Common;
using MiniMes.Contracts.Execution;
using MiniMes.Domain.Execution;
using MiniMes.Domain.Planning;

namespace MiniMes.Application.Execution;

// Spec M3 step 9 (2.10): FPY and WIP of one work order. The two aggregations are plain SQL (ROW_NUMBER for
// "first scan of each serial at each step") in Infrastructure; the formulas are Domain (Yield).

public sealed class FirstPassRow
{
    public int Seq { get; init; }
    public int Entered { get; init; }
    public int PassedFirstTime { get; init; }
}

public sealed class WipCountRow
{
    public int CurrentSeq { get; init; }
    public string Status { get; init; } = string.Empty;
    public int Qty { get; init; }
}

public interface IExecutionReportQueries
{
    Task<IReadOnlyList<FirstPassRow>> FirstPassByStepAsync(int workOrderId, CancellationToken ct);
    Task<IReadOnlyList<WipCountRow>> WipAsync(int workOrderId, CancellationToken ct);
}

// ---------- FPY ----------

public sealed record GetFpyReportQuery(int WorkOrderId) : IRequest<FpyReportDto>;

public sealed class GetFpyReportHandler(IAppDbContext db, IExecutionReportQueries reports) : IRequestHandler<GetFpyReportQuery, FpyReportDto>
{
    public async Task<FpyReportDto> Handle(GetFpyReportQuery q, CancellationToken ct)
    {
        var woNo = await db.WoNoAsync(q.WorkOrderId, ct);   // 404
        var steps = await db.StepsAsync(q.WorkOrderId, ct);
        var firstPass = (await reports.FirstPassByStepAsync(q.WorkOrderId, ct)).ToDictionary(r => r.Seq);
        // Every routing step, also those no board reached yet (0 entered, FPY null).
        var rows = steps.Select(s => firstPass.TryGetValue(s.Seq, out var r)
                ? new FpyStepDto(s.Seq, s.OperationCode, r.Entered, r.PassedFirstTime, Yield.Percent(r.PassedFirstTime, r.Entered))
                : new FpyStepDto(s.Seq, s.OperationCode, 0, 0, null))
            .ToList();

        var finished = await db.Serials.AsNoTracking()
            .Where(s => s.WorkOrderId == q.WorkOrderId && (s.Status == SerialStatus.Completed || s.Status == SerialStatus.Scrapped))
            .GroupBy(s => s.Status).Select(g => new { Status = g.Key, Qty = g.Count() }).ToListAsync(ct);
        var completed = finished.Where(f => f.Status == SerialStatus.Completed).Sum(f => f.Qty);
        var scrapped = finished.Where(f => f.Status == SerialStatus.Scrapped).Sum(f => f.Qty);

        return new FpyReportDto(q.WorkOrderId, woNo, rows, Yield.Rolled(rows.Select(r => (r.PassedFirstTime, r.Entered))),
            completed, scrapped, Yield.FinalYield(completed, scrapped));
    }
}

// ---------- WIP ----------

public sealed record GetWipReportQuery(int WorkOrderId) : IRequest<WipReportDto>;

public sealed class GetWipReportHandler(IAppDbContext db, IExecutionReportQueries reports) : IRequestHandler<GetWipReportQuery, WipReportDto>
{
    public async Task<WipReportDto> Handle(GetWipReportQuery q, CancellationToken ct)
    {
        var woNo = await db.WoNoAsync(q.WorkOrderId, ct);   // 404
        var steps = await db.StepsAsync(q.WorkOrderId, ct);
        // Same "next step" as route check and the waiting-serials query: the smallest Seq above CurrentSeq.
        var rows = (await reports.WipAsync(q.WorkOrderId, ct))
            .Select(r => new WipRowDto(r.CurrentSeq,
                steps.FirstOrDefault(s => s.Seq == r.CurrentSeq)?.OperationCode ?? "-",
                steps.FirstOrDefault(s => s.Seq > r.CurrentSeq)?.OperationCode,
                r.Status, r.Qty))
            .ToList();
        return new WipReportDto(q.WorkOrderId, woNo, rows, rows.Sum(r => r.Qty));
    }
}

internal static class ReportReads
{
    public sealed record Step(int Seq, string OperationCode);

    public static async Task<string> WoNoAsync(this IAppDbContext db, int workOrderId, CancellationToken ct)
        => await db.WorkOrders.AsNoTracking().Where(w => w.Id == workOrderId).Select(w => w.WoNo).SingleOrDefaultAsync(ct)
           ?? throw new KeyNotFoundException($"Work order {workOrderId} not found.");

    public static Task<List<Step>> StepsAsync(this IAppDbContext db, int workOrderId, CancellationToken ct)
        => db.WorkOrderOperations.AsNoTracking().Where(o => o.WorkOrderId == workOrderId).OrderBy(o => o.Seq)
            .Select(o => new Step(o.Seq, o.OperationCode)).ToListAsync(ct);
}
