using FluentValidation;
using FluentValidation.Results;
using MediatR;
using Microsoft.EntityFrameworkCore;
using MiniMes.Application.Common;
using MiniMes.Contracts.MasterData;
using MiniMes.Domain.Common;
using MiniMes.Domain.MasterData;
using MiniMes.Domain.MasterData.Routings;

namespace MiniMes.Application.MasterData.Routings;

// Same lifecycle as BOM (see Boms/*): one file here because each use case mirrors its BOM twin.

public sealed record GetRoutingsQuery(int ItemId) : IRequest<IReadOnlyList<RoutingSummaryDto>>;
public sealed record GetRoutingByIdQuery(int Id) : IRequest<RoutingDto>;
public sealed record CreateRoutingDraftCommand(int ItemId, bool CopyFromLatest) : IRequest<RoutingDto>;
public sealed record SaveRoutingStepsCommand(int RoutingId, IReadOnlyList<RoutingStepInput> Steps) : IRequest<RoutingDto>;
public sealed record ApproveRoutingCommand(int RoutingId, DateTime? EffectiveFromUtc) : IRequest<RoutingDto>;

public sealed class CreateRoutingDraftValidator : AbstractValidator<CreateRoutingDraftCommand>
{
    public CreateRoutingDraftValidator() => RuleFor(x => x.ItemId).GreaterThan(0);
}

public sealed class SaveRoutingStepsValidator : AbstractValidator<SaveRoutingStepsCommand>
{
    // Spec M1 1.5 (5): minutes typed as seconds, or a panel time instead of one board, gives absurd OEE.
    public const decimal MaxCycleTimeSec = 3600m;

    public SaveRoutingStepsValidator()
    {
        RuleFor(x => x.Steps).NotNull();
        RuleForEach(x => x.Steps).ChildRules(step =>
        {
            step.RuleFor(s => s.Seq).GreaterThan(0);
            step.RuleFor(s => s.OperationCode).NotEmpty().MaximumLength(MasterDataRules.CodeMaxLength)
                .Matches(MasterDataRules.CodePattern);
            step.RuleFor(s => s.WorkCenterId).GreaterThan(0);
            step.RuleFor(s => s.IdealCycleTimeSec).GreaterThan(0).LessThanOrEqualTo(MaxCycleTimeSec)
                .WithMessage($"IdealCycleTimeSec is seconds per unit: 0 < value <= {MaxCycleTimeSec}.");
            step.RuleFor(s => s.SopUrl).MaximumLength(RoutingStep.SopUrlMaxLength);
        });
        RuleFor(x => x.Steps)
            .Must(steps => steps.Select(s => s.Seq).Distinct().Count() == steps.Count)
            .When(x => x.Steps is not null)
            .WithMessage("Seq must be unique: route check (M3) needs to know which step comes first.");
    }
}

public sealed class ApproveRoutingValidator : AbstractValidator<ApproveRoutingCommand>
{
    public ApproveRoutingValidator(TimeProvider time) => RuleFor(x => x.EffectiveFromUtc).NotInThePast(time);
}

public sealed class GetRoutingsHandler(IAppDbContext db) : IRequestHandler<GetRoutingsQuery, IReadOnlyList<RoutingSummaryDto>>
{
    public async Task<IReadOnlyList<RoutingSummaryDto>> Handle(GetRoutingsQuery q, CancellationToken ct)
        => await db.Routings.AsNoTracking()
            .Where(r => r.ItemId == q.ItemId)
            .OrderByDescending(r => r.Version)
            .Join(db.Items, r => r.ItemId, i => i.Id, (r, i) => new RoutingSummaryDto(
                r.Id, r.ItemId, i.Code, r.Version, r.Status.ToString(), r.EffectiveFrom, r.EffectiveTo, r.Steps.Count))
            .ToListAsync(ct);
}

public sealed class GetRoutingByIdHandler(IAppDbContext db) : IRequestHandler<GetRoutingByIdQuery, RoutingDto>
{
    public Task<RoutingDto> Handle(GetRoutingByIdQuery q, CancellationToken ct) => db.GetRoutingDtoAsync(q.Id, ct);
}

public sealed class CreateRoutingDraftHandler(IAppDbContext db) : IRequestHandler<CreateRoutingDraftCommand, RoutingDto>
{
    public async Task<RoutingDto> Handle(CreateRoutingDraftCommand cmd, CancellationToken ct)
    {
        var item = await db.LoadParentItemAsync(cmd.ItemId, "routing", ct);

        var latest = await db.Routings.AsNoTracking().Include(r => r.Steps)
            .Where(r => r.ItemId == item.Id).OrderByDescending(r => r.Version).FirstOrDefaultAsync(ct);
        if (latest?.Status == VersionStatus.Draft)
            throw new DomainException(
                $"Routing version {latest.Version} of '{item.Code}' is still Draft; edit or approve it instead of starting another.");

        var draft = Routing.CreateDraft(item.Id, (latest?.Version ?? 0) + 1);
        if (cmd.CopyFromLatest && latest is not null)
            foreach (var s in latest.Steps.OrderBy(s => s.Seq))
                draft.AddStep(s.Seq, s.OperationCode, s.WorkCenterId, s.IdealCycleTimeSec, s.IsInspection, s.IsMandatory, s.SopUrl);

        db.Routings.Add(draft);
        await db.SaveChangesAsync(ct);
        return await db.GetRoutingDtoAsync(draft.Id, ct);
    }
}

public sealed class SaveRoutingStepsHandler(IAppDbContext db) : IRequestHandler<SaveRoutingStepsCommand, RoutingDto>
{
    public async Task<RoutingDto> Handle(SaveRoutingStepsCommand cmd, CancellationToken ct)
    {
        var routing = await db.LoadRoutingAsync(cmd.RoutingId, ct);
        routing.ClearSteps(); // Approved -> 422

        var wcIds = cmd.Steps.Select(s => s.WorkCenterId).Distinct().ToList();
        var workCenters = await db.WorkCenters.AsNoTracking().Where(w => wcIds.Contains(w.Id)).ToDictionaryAsync(w => w.Id, ct);
        var missing = wcIds.Where(id => !workCenters.ContainsKey(id)).ToList();
        if (missing.Count > 0)
            throw new ValidationException(
                [new ValidationFailure("Steps", $"Unknown work center id(s): {string.Join(", ", missing)}.")]);
        var inactive = workCenters.Values.Where(w => !w.IsActive).Select(w => w.Code).ToList();
        if (inactive.Count > 0)
            throw new DomainException($"Inactive work center(s) cannot be used: {string.Join(", ", inactive)}.");

        foreach (var s in cmd.Steps.OrderBy(s => s.Seq))
            routing.AddStep(s.Seq, s.OperationCode, s.WorkCenterId, s.IdealCycleTimeSec, s.IsInspection, s.IsMandatory, s.SopUrl);
        await db.SaveChangesAsync(ct);
        return await db.GetRoutingDtoAsync(routing.Id, ct);
    }
}

public sealed class ApproveRoutingHandler(IAppDbContext db, TimeProvider time) : IRequestHandler<ApproveRoutingCommand, RoutingDto>
{
    public async Task<RoutingDto> Handle(ApproveRoutingCommand cmd, CancellationToken ct)
    {
        var routing = await db.LoadRoutingAsync(cmd.RoutingId, ct);

        var wcIds = routing.Steps.Select(s => s.WorkCenterId).ToList();
        var inactive = await db.WorkCenters.AsNoTracking()
            .Where(w => wcIds.Contains(w.Id) && !w.IsActive).Select(w => w.Code).ToListAsync(ct);
        if (inactive.Count > 0)
            throw new DomainException($"Routing uses inactive work center(s): {string.Join(", ", inactive)}.");

        var previous = await db.Routings.SingleOrDefaultAsync(
            r => r.ItemId == routing.ItemId && r.Status == VersionStatus.Approved && r.EffectiveTo == null, ct);

        routing.Approve(cmd.EffectiveFromUtc?.ToUniversalTime() ?? time.GetUtcNow().UtcDateTime, previous);
        await db.SaveChangesAsync(ct);
        return await db.GetRoutingDtoAsync(routing.Id, ct);
    }
}
