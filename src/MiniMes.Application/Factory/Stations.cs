using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using MiniMes.Application.Common;
using MiniMes.Contracts.Factory;
using MiniMes.Domain.Common;
using MiniMes.Domain.Factory;

namespace MiniMes.Application.Factory;

public sealed record GetStationsQuery(int? LineId) : IRequest<IReadOnlyList<StationDto>>;
public sealed record GetStationByIdQuery(int Id) : IRequest<StationDto>;
public sealed record CreateStationCommand(int LineId, string Code, string Name, string Type) : IRequest<StationDto>;
public sealed record UpdateStationCommand(int Id, string Name, string Type, bool IsActive) : IRequest<StationDto>;

public sealed class CreateStationValidator : AbstractValidator<CreateStationCommand>
{
    public CreateStationValidator()
    {
        RuleFor(x => x.LineId).GreaterThan(0);
        RuleFor(x => x.Code).FactoryCode();
        RuleFor(x => x.Name).FactoryName();
        RuleFor(x => x.Type).StationTypeName();
    }
}

public sealed class UpdateStationValidator : AbstractValidator<UpdateStationCommand>
{
    public UpdateStationValidator()
    {
        RuleFor(x => x.Name).FactoryName();
        RuleFor(x => x.Type).StationTypeName();
    }
}

public sealed class GetStationsHandler(IAppDbContext db) : IRequestHandler<GetStationsQuery, IReadOnlyList<StationDto>>
{
    public async Task<IReadOnlyList<StationDto>> Handle(GetStationsQuery q, CancellationToken ct)
        => (await db.Stations.AsNoTracking()
                .Where(x => q.LineId == null || x.LineId == q.LineId)
                .OrderBy(x => x.Code).ToListAsync(ct))
            .Select(x => x.ToDto()).ToList();
}

public sealed class GetStationByIdHandler(IAppDbContext db) : IRequestHandler<GetStationByIdQuery, StationDto>
{
    public async Task<StationDto> Handle(GetStationByIdQuery q, CancellationToken ct)
        => (await db.Stations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == q.Id, ct)
            ?? throw new KeyNotFoundException($"Station {q.Id} not found.")).ToDto();
}

public sealed class CreateStationHandler(IAppDbContext db) : IRequestHandler<CreateStationCommand, StationDto>
{
    public async Task<StationDto> Handle(CreateStationCommand cmd, CancellationToken ct)
    {
        var line = await db.Lines.SingleOrDefaultAsync(x => x.Id == cmd.LineId, ct)
                   ?? throw new KeyNotFoundException($"Line {cmd.LineId} not found.");

        // Station code is unique across the whole system (scans reference it directly).
        if (await db.Stations.AnyAsync(x => x.Code == cmd.Code, ct))
            throw new ConflictException($"Station '{cmd.Code}' already exists.");

        var station = line.AddStation(cmd.Code, cmd.Name, Enum.Parse<StationType>(cmd.Type)); // inactive line -> 422
        await db.SaveChangesAsync(ct);
        return station.ToDto();
    }
}

public sealed class UpdateStationHandler(IAppDbContext db) : IRequestHandler<UpdateStationCommand, StationDto>
{
    public async Task<StationDto> Handle(UpdateStationCommand cmd, CancellationToken ct)
    {
        var station = await db.Stations.SingleOrDefaultAsync(x => x.Id == cmd.Id, ct)
                      ?? throw new KeyNotFoundException($"Station {cmd.Id} not found.");
        station.Rename(cmd.Name);
        station.ChangeType(Enum.Parse<StationType>(cmd.Type));
        station.SetActive(cmd.IsActive);
        await db.SaveChangesAsync(ct);
        return station.ToDto();
    }
}
