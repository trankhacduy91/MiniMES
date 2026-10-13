using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using MiniMes.Application.Common;
using MiniMes.Contracts.MasterData;
using MiniMes.Domain.Common;
using MiniMes.Domain.MasterData;
using MiniMes.Domain.MasterData.Items;

namespace MiniMes.Application.MasterData.Items;

public sealed record CreateItemCommand(
    string Code, string Name, string Type, string Uom,
    bool IsSerialized, bool IsLotControlled, bool SkipIqc) : IRequest<ItemDto>;

// Request shape checks (400, one message per field). The same rules live in Item.Create as a safety
// net for other callers (seeder, import); here they reuse the Domain constants so they cannot drift apart.
public sealed class CreateItemValidator : AbstractValidator<CreateItemCommand>
{
    public CreateItemValidator()
    {
        RuleFor(x => x.Code).NotEmpty().MaximumLength(MasterDataRules.CodeMaxLength)
            .Matches(MasterDataRules.CodePattern)
            .WithMessage("Code may contain only upper-case letters, digits, '-', '_' and '.'.");
        RuleFor(x => x.Name).NotEmpty().MaximumLength(MasterDataRules.NameMaxLength);
        RuleFor(x => x.Type).IsEnumName(typeof(ItemType))
            .WithMessage("Type must be Raw, Semi or Finished.");
        RuleFor(x => x.Uom).Must(Units.IsValid)
            .WithMessage($"Unit must be one of: {string.Join(", ", Units.All)}.");

        When(x => Enum.TryParse<ItemType>(x.Type, out _), () =>
        {
            RuleFor(x => x.IsSerialized)
                .Must((cmd, serialized) => serialized == Expected(cmd).IsSerialized)
                .WithMessage(cmd => Expected(cmd).IsSerialized
                    ? $"A {cmd.Type} item must be serialized."
                    : $"A {cmd.Type} item cannot be serialized.");
            RuleFor(x => x.IsLotControlled)
                .Must((cmd, lot) => lot == Expected(cmd).IsLotControlled)
                .WithMessage(cmd => Expected(cmd).IsLotControlled
                    ? $"A {cmd.Type} item must be lot-controlled."
                    : $"A {cmd.Type} item cannot be lot-controlled.");
            RuleFor(x => x.SkipIqc).Equal(false).When(x => x.Type != nameof(ItemType.Raw))
                .WithMessage("SkipIqc is only valid for Raw items.");
        });
    }

    private static (bool IsSerialized, bool IsLotControlled) Expected(CreateItemCommand cmd)
        => Item.ExpectedTraceability(Enum.Parse<ItemType>(cmd.Type));
}

public sealed class CreateItemHandler(IAppDbContext db) : IRequestHandler<CreateItemCommand, ItemDto>
{
    public async Task<ItemDto> Handle(CreateItemCommand cmd, CancellationToken ct)
    {
        // Friendly message for the common case; the unique index still catches a race (-> 409 too).
        if (await db.Items.AnyAsync(i => i.Code == cmd.Code, ct))
            throw new ConflictException($"Item code '{cmd.Code}' already exists.");

        var item = Item.Create(cmd.Code, cmd.Name, Enum.Parse<ItemType>(cmd.Type), cmd.Uom,
            cmd.IsSerialized, cmd.IsLotControlled, cmd.SkipIqc);
        db.Items.Add(item);
        await db.SaveChangesAsync(ct);
        return item.ToDto();
    }
}
