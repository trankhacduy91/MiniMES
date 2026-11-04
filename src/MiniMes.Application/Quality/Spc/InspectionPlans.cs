using MediatR;
using Microsoft.EntityFrameworkCore;
using MiniMes.Application.Common;
using MiniMes.Contracts.Quality;

namespace MiniMes.Application.Quality.Spc;

// M7 step 7: the plans a chart can show (active only, by code).
public sealed record GetInspectionPlansQuery : IRequest<IReadOnlyList<InspectionPlanDto>>;

public sealed class GetInspectionPlansHandler(IAppDbContext db) : IRequestHandler<GetInspectionPlansQuery, IReadOnlyList<InspectionPlanDto>>
{
    public async Task<IReadOnlyList<InspectionPlanDto>> Handle(GetInspectionPlansQuery q, CancellationToken ct)
        => await (from p in db.InspectionPlans.AsNoTracking()
                  where p.IsActive
                  join i in db.Items on p.ItemId equals i.Id into ij
                  from i in ij.DefaultIfEmpty()
                  let tag = (from t in db.MachineTags
                             join m in db.Machines on t.MachineId equals m.Id
                             where t.SpcCharacteristicId == p.Id && t.IsActive
                             select m.Code + "/" + t.TagName).FirstOrDefault()
                  orderby p.Code
                  select new InspectionPlanDto(p.Id, p.Code, p.Name, p.Unit, p.Lsl, p.Usl, p.Target, i.Code, p.OperationCode, tag,
                      db.ControlLimitSets.Any(c => c.InspectionPlanId == p.Id && c.IsActive)))
            .ToListAsync(ct);
}
