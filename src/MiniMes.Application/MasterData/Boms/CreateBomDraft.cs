using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using MiniMes.Application.Common;
using MiniMes.Contracts.MasterData;
using MiniMes.Domain.Common;
using MiniMes.Domain.MasterData;
using MiniMes.Domain.MasterData.Boms;

namespace MiniMes.Application.MasterData.Boms;

// New version = max + 1, as Draft. One Draft per item at a time (also a filtered unique index):
// two drafts of the same BOM would compete to become "the next version".
public sealed record CreateBomDraftCommand(int ItemId, bool CopyFromLatest) : IRequest<BomDto>;

public sealed class CreateBomDraftValidator : AbstractValidator<CreateBomDraftCommand>
{
    public CreateBomDraftValidator() => RuleFor(x => x.ItemId).GreaterThan(0);
}

public sealed class CreateBomDraftHandler(IAppDbContext db) : IRequestHandler<CreateBomDraftCommand, BomDto>
{
    public async Task<BomDto> Handle(CreateBomDraftCommand cmd, CancellationToken ct)
    {
        var item = await db.LoadParentItemAsync(cmd.ItemId, "BOM", ct);

        var latest = await db.Boms.AsNoTracking().Include(b => b.Lines)
            .Where(b => b.ItemId == item.Id).OrderByDescending(b => b.Version).FirstOrDefaultAsync(ct);
        if (latest?.Status == VersionStatus.Draft)
            throw new DomainException(
                $"BOM version {latest.Version} of '{item.Code}' is still Draft; edit or approve it instead of starting another.");

        var draft = Bom.CreateDraft(item.Id, (latest?.Version ?? 0) + 1);
        if (cmd.CopyFromLatest && latest is not null)
            foreach (var l in latest.Lines)
                draft.AddLine(l.ComponentItemId, l.QtyPer, l.ScrapRate, l.OperationSeq);

        db.Boms.Add(draft);
        await db.SaveChangesAsync(ct); // a racing request hits the unique indexes -> 409
        return await db.GetBomDtoAsync(draft.Id, ct);
    }
}
