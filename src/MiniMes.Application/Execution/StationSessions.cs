using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using MiniMes.Application.Common;
using MiniMes.Contracts.Execution;
using MiniMes.Domain.Execution;

namespace MiniMes.Application.Execution;

// Spec M3 step 5: who works at which station on which WO. A scan needs an open session (Scan handler).

// ---------- Open ----------

public sealed record OpenStationSessionCommand(int StationId, int WorkOrderId) : IRequest<StationSessionDto>;

public sealed class OpenStationSessionValidator : AbstractValidator<OpenStationSessionCommand>
{
    public OpenStationSessionValidator() => RuleFor(x => x.WorkOrderId).GreaterThan(0);
}

public sealed class OpenStationSessionHandler(IAppDbContext db, ICurrentUser user, TimeProvider time)
    : IRequestHandler<OpenStationSessionCommand, StationSessionDto>
{
    public async Task<StationSessionDto> Handle(OpenStationSessionCommand cmd, CancellationToken ct)
    {
        var station = await db.Stations.AsNoTracking().Where(s => s.Id == cmd.StationId && s.IsActive)
                          .Select(s => new { s.Id, s.LineId }).SingleOrDefaultAsync(ct)
                      ?? throw new KeyNotFoundException($"Station {cmd.StationId} not found or inactive.");
        var wo = await db.WorkOrders.AsNoTracking().Where(w => w.Id == cmd.WorkOrderId)
                     .Select(w => new { w.LineId, w.WoNo, w.Status }).SingleOrDefaultAsync(ct)
                 ?? throw new KeyNotFoundException($"Work order {cmd.WorkOrderId} not found.");
        StationSession.EnsureCanRun(station.LineId, wo.LineId, wo.WoNo, wo.Status);   // 422
        var operatorCode = user.EmployeeCode ?? throw new InvalidOperationException("A session needs a logged-in user.");
        var now = time.GetUtcNow().UtcDateTime;

        // Close-then-open in one transaction: the next shift's login ends the previous one's session, so no one
        // scans under someone else's name. Two kiosks logging in at the same instant: the filtered unique index
        // (one open session per station) lets one win, the other gets 409.
        await using var tx = await db.BeginTransactionAsync(ct);
        foreach (var open in await db.StationSessions.Where(s => s.StationId == station.Id && s.ClosedAt == null).ToListAsync(ct))
            open.Close(now);
        await db.SaveChangesAsync(ct);   // the old row must be closed before the index sees the new one
        var session = new StationSession(station.Id, cmd.WorkOrderId, operatorCode, now);
        db.StationSessions.Add(session);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return await db.GetSessionDtoAsync(session.Id, ct);
    }
}

// ---------- Close (logout / end of shift) ----------

public sealed record CloseStationSessionCommand(int StationId, long SessionId) : IRequest;

public sealed class CloseStationSessionHandler(IAppDbContext db, TimeProvider time) : IRequestHandler<CloseStationSessionCommand>
{
    public async Task Handle(CloseStationSessionCommand cmd, CancellationToken ct)
    {
        var session = await db.StationSessions.SingleOrDefaultAsync(s => s.Id == cmd.SessionId && s.StationId == cmd.StationId, ct)
                      ?? throw new KeyNotFoundException($"Session {cmd.SessionId} not found at station {cmd.StationId}.");
        session.Close(time.GetUtcNow().UtcDateTime);   // already closed (e.g. by the next login) -> 422
        await db.SaveChangesAsync(ct);
    }
}

internal static class StationSessionReads
{
    public static Task<StationSessionDto> GetSessionDtoAsync(this IAppDbContext db, long id, CancellationToken ct)
        => (from s in db.StationSessions.AsNoTracking()
            join st in db.Stations on s.StationId equals st.Id
            join w in db.WorkOrders on s.WorkOrderId equals w.Id
            where s.Id == id
            select new StationSessionDto(s.Id, s.StationId, st.Code, s.WorkOrderId, w.WoNo, s.OperatorCode, s.OpenedAt, s.ClosedAt))
           .SingleAsync(ct);
}
