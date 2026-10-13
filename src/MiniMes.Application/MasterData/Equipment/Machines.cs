using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using MiniMes.Application.Common;
using MiniMes.Contracts.MasterData;
using MiniMes.Domain.Common;
using MiniMes.Domain.MasterData.Equipment;

namespace MiniMes.Application.MasterData.Equipment;

public sealed record GetMachinesQuery(int? WorkCenterId, bool? IsActive) : IRequest<IReadOnlyList<MachineDto>>;
public sealed record GetMachineByIdQuery(int Id) : IRequest<MachineDto>;
public sealed record CreateMachineCommand(
    int WorkCenterId, string Code, string Name, string Type, string MqttTopicKey, string DataSource) : IRequest<MachineDto>;
public sealed record UpdateMachineCommand(int Id, string Name, string Type, string DataSource) : IRequest<MachineDto>;
public sealed record DeactivateMachineCommand(int Id) : IRequest;

public sealed class CreateMachineValidator : AbstractValidator<CreateMachineCommand>
{
    public CreateMachineValidator()
    {
        RuleFor(x => x.WorkCenterId).GreaterThan(0);
        RuleFor(x => x.Code).MasterCode();
        RuleFor(x => x.Name).MasterName();
        RuleFor(x => x.Type).NotEmpty().MaximumLength(50);
        RuleFor(x => x.MqttTopicKey).MqttTopicKey();
        RuleFor(x => x.DataSource).DataSourceName();
    }
}

public sealed class UpdateMachineValidator : AbstractValidator<UpdateMachineCommand>
{
    public UpdateMachineValidator()
    {
        RuleFor(x => x.Name).MasterName();
        RuleFor(x => x.Type).NotEmpty().MaximumLength(50);
        RuleFor(x => x.DataSource).DataSourceName();
    }
}

internal static class MachineMapping
{
    public static MachineDto ToDto(this Machine m)
        => new(m.Id, m.WorkCenterId, m.Code, m.Name, m.Type, m.MqttTopicKey, m.DataSource.ToString(), m.IsActive);

    public static async Task<Machine> LoadMachineAsync(this IAppDbContext db, int id, CancellationToken ct)
        => await db.Machines.SingleOrDefaultAsync(m => m.Id == id, ct)
           ?? throw new KeyNotFoundException($"Machine {id} not found.");
}

public sealed class GetMachinesHandler(IAppDbContext db) : IRequestHandler<GetMachinesQuery, IReadOnlyList<MachineDto>>
{
    public async Task<IReadOnlyList<MachineDto>> Handle(GetMachinesQuery q, CancellationToken ct)
        => (await db.Machines.AsNoTracking()
                .Where(m => (q.WorkCenterId == null || m.WorkCenterId == q.WorkCenterId) && (q.IsActive == null || m.IsActive == q.IsActive))
                .OrderBy(m => m.Code).ToListAsync(ct))
            .Select(m => m.ToDto()).ToList();
}

public sealed class GetMachineByIdHandler(IAppDbContext db) : IRequestHandler<GetMachineByIdQuery, MachineDto>
{
    public async Task<MachineDto> Handle(GetMachineByIdQuery q, CancellationToken ct) => (await db.LoadMachineAsync(q.Id, ct)).ToDto();
}

public sealed class CreateMachineHandler(IAppDbContext db) : IRequestHandler<CreateMachineCommand, MachineDto>
{
    public async Task<MachineDto> Handle(CreateMachineCommand cmd, CancellationToken ct)
    {
        var wc = await db.LoadWorkCenterAsync(cmd.WorkCenterId, ct);
        if (!wc.IsActive) throw new DomainException($"Work center '{wc.Code}' is inactive.");
        if (await db.Machines.AnyAsync(m => m.Code == cmd.Code, ct))
            throw new ConflictException($"Machine '{cmd.Code}' already exists.");
        // Two machines on one topic would mix their counters in OEE (M4/M5). Unique index backs this up.
        if (await db.Machines.AnyAsync(m => m.MqttTopicKey == cmd.MqttTopicKey, ct))
            throw new ConflictException($"MqttTopicKey '{cmd.MqttTopicKey}' is already used by another machine.");

        var machine = new Machine(wc.Id, cmd.Code, cmd.Name, cmd.Type, cmd.MqttTopicKey, Enum.Parse<MachineDataSource>(cmd.DataSource));
        db.Machines.Add(machine);
        await db.SaveChangesAsync(ct);
        return machine.ToDto();
    }
}

public sealed class UpdateMachineHandler(IAppDbContext db) : IRequestHandler<UpdateMachineCommand, MachineDto>
{
    public async Task<MachineDto> Handle(UpdateMachineCommand cmd, CancellationToken ct)
    {
        var machine = await db.LoadMachineAsync(cmd.Id, ct);
        machine.Update(cmd.Name, cmd.Type, Enum.Parse<MachineDataSource>(cmd.DataSource));
        await db.SaveChangesAsync(ct);
        return machine.ToDto();
    }
}

public sealed class DeactivateMachineHandler(IAppDbContext db) : IRequestHandler<DeactivateMachineCommand>
{
    public async Task Handle(DeactivateMachineCommand cmd, CancellationToken ct)
    {
        var machine = await db.LoadMachineAsync(cmd.Id, ct);
        machine.SetActive(false);
        await db.SaveChangesAsync(ct);
    }
}
