using MediatR;
using Microsoft.EntityFrameworkCore;
using MiniMes.Application.Common;
using MiniMes.Contracts.Realtime;
using WireQuality = MiniMes.Contracts.Machines.DataQuality;
using WireState = MiniMes.Contracts.Machines.MachineState;

namespace MiniMes.Application.Machines;

// Spec M4 2.6: the snapshot a dashboard loads first and again after every SignalR reconnect. Master data from the
// DB, live values from the runtime store (RAM): no MachineEvents scan.
// Line codes are unique per area; the MVP has one area, so the code is enough (same key as the SignalR group).
public sealed record GetLineStatusQuery(string LineCode) : IRequest<LineStatusDto>;

public sealed class GetLineStatusHandler(IAppDbContext db, MachineRuntimeStore store, GatewayStatusStore gateways, TimeProvider time)
    : IRequestHandler<GetLineStatusQuery, LineStatusDto>
{
    public async Task<LineStatusDto> Handle(GetLineStatusQuery q, CancellationToken ct)
    {
        var lineId = await db.Lines.AsNoTracking().Where(l => l.Code == q.LineCode).OrderBy(l => l.Id).Select(l => (int?)l.Id).FirstOrDefaultAsync(ct)
            ?? throw new KeyNotFoundException($"Line {q.LineCode} not found.");

        var machines = await (
            from m in db.Machines.AsNoTracking()
            join wc in db.WorkCenters on m.WorkCenterId equals wc.Id
            where wc.LineId == lineId && m.IsActive
            orderby m.Code
            select new { m.Id, m.Code, m.Name, m.Type, m.DataSource })
            .ToListAsync(ct);

        var now = time.GetUtcNow().UtcDateTime;
        return new LineStatusDto(q.LineCode, machines.Select(m =>
        {
            var rt = store.Get(m.Id);
            return new MachineStatusDto(m.Id, m.Code, m.Name, m.Type, m.DataSource.ToString(),
                rt?.State is { } s ? Enum.Parse<WireState>(s.ToString()) : null, rt?.StateSince, rt?.LastCounter,
                rt is null ? 0 : store.CountThisHour(rt, now), rt?.LastSeenAt,
                rt?.Process.Select(p => new ProcessReadingDto(p.Tag, p.Unit, p.Value, Enum.Parse<WireQuality>(p.Quality.ToString()), p.SourceTs)).ToList() ?? []);
        }).ToList(),
            gateways.ForMachines(machines.Select(m => m.Code)));   // [M4b] the gateway cards of this line
    }
}
