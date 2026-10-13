using FluentValidation;
using FluentValidation.Results;
using MediatR;
using Microsoft.EntityFrameworkCore;
using MiniMes.Application.Common;
using MiniMes.Contracts.MasterData;
using MiniMes.Domain.Common;

namespace MiniMes.Application.MasterData.Boms;

// Replaces all lines of a Draft. Approved -> 422 (locked: make a new version).
public sealed record SaveBomLinesCommand(int BomId, IReadOnlyList<BomLineInput> Lines) : IRequest<BomDto>;

public sealed class SaveBomLinesValidator : AbstractValidator<SaveBomLinesCommand>
{
    public SaveBomLinesValidator()
    {
        RuleFor(x => x.Lines).NotNull();
        RuleForEach(x => x.Lines).ChildRules(line =>
        {
            line.RuleFor(l => l.ComponentItemId).GreaterThan(0);
            line.RuleFor(l => l.QtyPer).GreaterThan(0);
            line.RuleFor(l => l.ScrapRate).GreaterThanOrEqualTo(0).LessThan(1)
                .WithMessage("ScrapRate is a fraction: 0 <= ScrapRate < 1 (2% = 0.02).");
            line.RuleFor(l => l.OperationSeq).GreaterThan(0);
        });
        RuleFor(x => x.Lines)
            .Must(lines => lines.Select(l => l.ComponentItemId).Distinct().Count() == lines.Count)
            .When(x => x.Lines is not null)
            .WithMessage("A component may appear only once; change its quantity instead.");
    }
}

public sealed class SaveBomLinesHandler(IAppDbContext db) : IRequestHandler<SaveBomLinesCommand, BomDto>
{
    public async Task<BomDto> Handle(SaveBomLinesCommand cmd, CancellationToken ct)
    {
        var bom = await db.LoadBomAsync(cmd.BomId, ct);
        bom.ClearLines(); // Domain: only a Draft can change (422), checked before anything else

        var ids = cmd.Lines.Select(l => l.ComponentItemId).ToList();
        var components = await db.Items.AsNoTracking().Where(i => ids.Contains(i.Id)).ToDictionaryAsync(i => i.Id, ct);
        var missing = ids.Where(id => !components.ContainsKey(id)).ToList();
        if (missing.Count > 0)
            throw new ValidationException(
                [new ValidationFailure("Lines", $"Unknown component item id(s): {string.Join(", ", missing)}.")]);

        // Rule 14: an inactive item cannot be picked for a new BOM.
        var inactive = components.Values.Where(i => !i.IsActive).Select(i => i.Code).ToList();
        if (inactive.Count > 0)
            throw new DomainException($"Inactive component(s) cannot be used: {string.Join(", ", inactive)}.");

        // 400 like other input errors: the user fixes it in the editor, so the message names the loop.
        if (await db.FindCycleAsync(bom.ItemId, ids, ct) is { } cycle)
            throw new ValidationException(
                [new ValidationFailure("Lines", $"These lines would create a BOM cycle: {cycle}.")]);

        foreach (var l in cmd.Lines)
            bom.AddLine(l.ComponentItemId, l.QtyPer, l.ScrapRate, l.OperationSeq);
        await db.SaveChangesAsync(ct);
        return await db.GetBomDtoAsync(bom.Id, ct);
    }
}
