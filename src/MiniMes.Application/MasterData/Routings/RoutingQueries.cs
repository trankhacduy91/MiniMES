using Microsoft.EntityFrameworkCore;
using MiniMes.Application.Common;
using MiniMes.Contracts.MasterData;
using MiniMes.Domain.MasterData.Routings;

namespace MiniMes.Application.MasterData.Routings;

internal static class RoutingQueries
{
    public static async Task<Routing> LoadRoutingAsync(this IAppDbContext db, int id, CancellationToken ct)
        => await db.Routings.Include(r => r.Steps).SingleOrDefaultAsync(r => r.Id == id, ct)
           ?? throw new KeyNotFoundException($"Routing {id} not found.");

    public static async Task<RoutingDto> GetRoutingDtoAsync(this IAppDbContext db, int id, CancellationToken ct)
    {
        var routing = await db.Routings.AsNoTracking().Include(r => r.Steps).SingleOrDefaultAsync(r => r.Id == id, ct)
                      ?? throw new KeyNotFoundException($"Routing {id} not found.");
        var itemCode = await db.Items.Where(i => i.Id == routing.ItemId).Select(i => i.Code).SingleAsync(ct);
        var wcIds = routing.Steps.Select(s => s.WorkCenterId).Distinct().ToList();
        var wcCodes = await db.WorkCenters.AsNoTracking().Where(w => wcIds.Contains(w.Id))
            .ToDictionaryAsync(w => w.Id, w => w.Code, ct);

        var steps = routing.Steps.OrderBy(s => s.Seq)
            .Select(s => new RoutingStepDto(s.Seq, s.OperationCode, s.WorkCenterId, wcCodes[s.WorkCenterId],
                s.IdealCycleTimeSec, s.IsInspection, s.IsMandatory, s.SopUrl))
            .ToList();
        return new RoutingDto(routing.Id, routing.ItemId, itemCode, routing.Version, routing.Status.ToString(),
            routing.EffectiveFrom, routing.EffectiveTo, steps);
    }
}
