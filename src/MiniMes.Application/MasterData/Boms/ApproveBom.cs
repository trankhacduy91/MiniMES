using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using MiniMes.Application.Common;
using MiniMes.Contracts.MasterData;
using MiniMes.Domain.Common;
using MiniMes.Domain.MasterData;

namespace MiniMes.Application.MasterData.Boms;

public sealed record ApproveBomCommand(int BomId, DateTime? EffectiveFromUtc) : IRequest<BomDto>;

public sealed class ApproveBomValidator : AbstractValidator<ApproveBomCommand>
{
    public ApproveBomValidator(TimeProvider time) => RuleFor(x => x.EffectiveFromUtc).NotInThePast(time);
}

// Rule 7: not empty (Domain), every component active, no cycle. Checked again here because other
// BOMs may have changed since the lines were saved. Closing the previous version and approving this
// one go in a single SaveChanges = one transaction: nobody ever sees two open versions, or none.
// The filtered index UX_Boms_OneOpenApprovedPerItem is checked per statement: EF sends the two UPDATEs
// ordered by key, so the older version (lower Id) is closed before the new one opens.
public sealed class ApproveBomHandler(IAppDbContext db, TimeProvider time) : IRequestHandler<ApproveBomCommand, BomDto>
{
    public async Task<BomDto> Handle(ApproveBomCommand cmd, CancellationToken ct)
    {
        var bom = await db.LoadBomAsync(cmd.BomId, ct);
        var ids = bom.Lines.Select(l => l.ComponentItemId).ToList();

        var inactive = await db.Items.AsNoTracking()
            .Where(i => ids.Contains(i.Id) && !i.IsActive).Select(i => i.Code).ToListAsync(ct);
        if (inactive.Count > 0)
            throw new DomainException($"BOM uses inactive component(s): {string.Join(", ", inactive)}.");
        if (await db.FindCycleAsync(bom.ItemId, ids, ct) is { } cycle)
            throw new DomainException($"BOM would create a cycle: {cycle}.");

        var previous = await db.Boms.SingleOrDefaultAsync(
            b => b.ItemId == bom.ItemId && b.Status == VersionStatus.Approved && b.EffectiveTo == null, ct);

        bom.Approve(cmd.EffectiveFromUtc?.ToUniversalTime() ?? time.GetUtcNow().UtcDateTime, previous);
        await db.SaveChangesAsync(ct);
        return await db.GetBomDtoAsync(bom.Id, ct);
    }
}
