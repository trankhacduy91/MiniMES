using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using MiniMes.Application.Common;
using MiniMes.Contracts.MasterData;
using MiniMes.Domain.Common;
using MiniMes.Domain.MasterData.DefectCodes;

namespace MiniMes.Application.MasterData.Catalogs;

public sealed record GetDefectCodesQuery(bool? IsActive) : IRequest<IReadOnlyList<DefectCodeDto>>;
public sealed record GetDefectCodeByIdQuery(int Id) : IRequest<DefectCodeDto>;
public sealed record CreateDefectCodeCommand(string Code, string Name, string Category) : IRequest<DefectCodeDto>;
public sealed record UpdateDefectCodeCommand(int Id, string Name, string Category) : IRequest<DefectCodeDto>;
public sealed record DeactivateDefectCodeCommand(int Id) : IRequest;

public sealed class CreateDefectCodeValidator : AbstractValidator<CreateDefectCodeCommand>
{
    public CreateDefectCodeValidator()
    {
        RuleFor(x => x.Code).MasterCode();
        RuleFor(x => x.Name).MasterName();
        RuleFor(x => x.Category).NotEmpty().MaximumLength(DefectCode.CategoryMaxLength);
    }
}

public sealed class UpdateDefectCodeValidator : AbstractValidator<UpdateDefectCodeCommand>
{
    public UpdateDefectCodeValidator()
    {
        RuleFor(x => x.Name).MasterName();
        RuleFor(x => x.Category).NotEmpty().MaximumLength(DefectCode.CategoryMaxLength);
    }
}

internal static class DefectCodeMapping
{
    public static DefectCodeDto ToDto(this DefectCode d) => new(d.Id, d.Code, d.Name, d.Category, d.IsActive);

    public static async Task<DefectCode> LoadDefectCodeAsync(this IAppDbContext db, int id, CancellationToken ct)
        => await db.DefectCodes.SingleOrDefaultAsync(d => d.Id == id, ct)
           ?? throw new KeyNotFoundException($"Defect code {id} not found.");
}

public sealed class GetDefectCodesHandler(IAppDbContext db) : IRequestHandler<GetDefectCodesQuery, IReadOnlyList<DefectCodeDto>>
{
    public async Task<IReadOnlyList<DefectCodeDto>> Handle(GetDefectCodesQuery q, CancellationToken ct)
        => (await db.DefectCodes.AsNoTracking().Where(d => q.IsActive == null || d.IsActive == q.IsActive)
                .OrderBy(d => d.Category).ThenBy(d => d.Code).ToListAsync(ct))
            .Select(d => d.ToDto()).ToList();
}

public sealed class GetDefectCodeByIdHandler(IAppDbContext db) : IRequestHandler<GetDefectCodeByIdQuery, DefectCodeDto>
{
    public async Task<DefectCodeDto> Handle(GetDefectCodeByIdQuery q, CancellationToken ct) => (await db.LoadDefectCodeAsync(q.Id, ct)).ToDto();
}

public sealed class CreateDefectCodeHandler(IAppDbContext db) : IRequestHandler<CreateDefectCodeCommand, DefectCodeDto>
{
    public async Task<DefectCodeDto> Handle(CreateDefectCodeCommand cmd, CancellationToken ct)
    {
        if (await db.DefectCodes.AnyAsync(d => d.Code == cmd.Code, ct))
            throw new ConflictException($"Defect code '{cmd.Code}' already exists.");

        var defect = new DefectCode(cmd.Code, cmd.Name, cmd.Category);
        db.DefectCodes.Add(defect);
        await db.SaveChangesAsync(ct);
        return defect.ToDto();
    }
}

public sealed class UpdateDefectCodeHandler(IAppDbContext db) : IRequestHandler<UpdateDefectCodeCommand, DefectCodeDto>
{
    public async Task<DefectCodeDto> Handle(UpdateDefectCodeCommand cmd, CancellationToken ct)
    {
        var defect = await db.LoadDefectCodeAsync(cmd.Id, ct);
        defect.Update(cmd.Name, cmd.Category);
        await db.SaveChangesAsync(ct);
        return defect.ToDto();
    }
}

public sealed class DeactivateDefectCodeHandler(IAppDbContext db) : IRequestHandler<DeactivateDefectCodeCommand>
{
    public async Task Handle(DeactivateDefectCodeCommand cmd, CancellationToken ct)
    {
        var defect = await db.LoadDefectCodeAsync(cmd.Id, ct);
        defect.SetActive(false); // hidden from pick lists, still shown in old NG records (rule 13)
        await db.SaveChangesAsync(ct);
    }
}
