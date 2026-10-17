using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using MiniMes.Application.Common;
using MiniMes.Contracts.Execution;
using MiniMes.Domain.Planning;

namespace MiniMes.Application.Execution;

// Spec M3 step 5b: which boards wait at this station. Same "next step" as route check: the smallest Seq above
// CurrentSeq (routings number 10, 20, 30), and the station matches if that step is on its work center.
// Empty when the station has no open session or no work center (Repair is off the routing).
public sealed record GetWaitingSerialsQuery(int StationId, int Take = 1) : IRequest<IReadOnlyList<WaitingSerialDto>>;

public sealed class GetWaitingSerialsValidator : AbstractValidator<GetWaitingSerialsQuery>
{
    public const int MaxTake = 100;

    public GetWaitingSerialsValidator() => RuleFor(x => x.Take).InclusiveBetween(1, MaxTake);
}

public sealed class GetWaitingSerialsHandler(IAppDbContext db) : IRequestHandler<GetWaitingSerialsQuery, IReadOnlyList<WaitingSerialDto>>
{
    public async Task<IReadOnlyList<WaitingSerialDto>> Handle(GetWaitingSerialsQuery q, CancellationToken ct)
    {
        var station = await db.LoadStationAsync(q.StationId, ct);   // 404
        if (station.WorkCenterId is not { } workCenterId) return [];
        if (await db.OpenSessionWoIdAsync(station.Id, ct) is not { } woId) return [];

        var wo = await db.WorkOrders.AsNoTracking().Where(w => w.Id == woId)
            .Select(w => new { w.FaiStatus, w.FaiSerialNo }).SingleAsync(ct);
        var boards = db.Serials.AsNoTracking()
            .Where(s => s.WorkOrderId == woId && (s.Status == SerialStatus.Created || s.Status == SerialStatus.InProcess));
        // [v2] FAI gate: while the first article is pending, only the sample may run (or, before one is chosen,
        // boards not started yet: the first one scanned becomes the sample). Otherwise the simulator would pick
        // board after board that route check refuses.
        if (wo.FaiStatus == FaiStatus.Pending)
            boards = wo.FaiSerialNo is { } sample ? boards.Where(s => s.SerialNo == sample) : boards.Where(s => s.CurrentSeq == 0);

        return await (from s in boards
                      let next = db.WorkOrderOperations.Where(o => o.WorkOrderId == woId && o.Seq > s.CurrentSeq)
                          .OrderBy(o => o.Seq).FirstOrDefault()
                      where next != null && next.WorkCenterId == workCenterId
                      orderby s.SerialNo
                      select new WaitingSerialDto(s.SerialNo, next.Seq, next.OperationCode))
                     .Take(q.Take).ToListAsync(ct);
    }
}
