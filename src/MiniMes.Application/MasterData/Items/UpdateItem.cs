using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using MiniMes.Application.Common;
using MiniMes.Contracts.MasterData;
using MiniMes.Domain.MasterData;

namespace MiniMes.Application.MasterData.Items;

// Only Name and SkipIqc are editable: Code, Type, Uom and traceability flags are fixed at creation.
public sealed record UpdateItemCommand(int Id, string Name, bool SkipIqc) : IRequest<ItemDto>;

public sealed class UpdateItemValidator : AbstractValidator<UpdateItemCommand>
{
    public UpdateItemValidator()
        => RuleFor(x => x.Name).NotEmpty().MaximumLength(MasterDataRules.NameMaxLength);
}

public sealed class UpdateItemHandler(IAppDbContext db) : IRequestHandler<UpdateItemCommand, ItemDto>
{
    public async Task<ItemDto> Handle(UpdateItemCommand cmd, CancellationToken ct)
    {
        var item = await db.Items.SingleOrDefaultAsync(i => i.Id == cmd.Id, ct)
                   ?? throw new KeyNotFoundException($"Item {cmd.Id} not found.");

        item.Rename(cmd.Name);
        item.SetSkipIqc(cmd.SkipIqc); // Domain rejects SkipIqc on non-Raw items (422): needs the stored Type
        await db.SaveChangesAsync(ct);
        return item.ToDto();
    }
}
