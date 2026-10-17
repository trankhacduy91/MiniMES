using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using MiniMes.Application.Common;
using MiniMes.Contracts.Common;
using MiniMes.Contracts.Planning;
using MiniMes.Domain.Common;
using MiniMes.Domain.Planning;

namespace MiniMes.Application.Planning;

// The release gate (spec M2 1.2d, rules R1-R5): check, freeze the recipe, create the serials, open the WO
// for the line. Everything is written by ONE SaveChanges, so it is one transaction: either the WO is
// Released with its snapshot and all its serials, or nothing changed.
public sealed record ReleaseWorkOrderCommand(int Id, string RowVersion) : IRequest<WorkOrderDetailDto>;

public sealed class ReleaseWorkOrderValidator : AbstractValidator<ReleaseWorkOrderCommand>
{
    public ReleaseWorkOrderValidator() => RuleFor(x => x.RowVersion).RowVersionRule();
}

public sealed class ReleaseWorkOrderHandler(
    IAppDbContext db, ISequenceNumbers sequences, PlantTimeZone plant, TimeProvider time)
    : IRequestHandler<ReleaseWorkOrderCommand, WorkOrderDetailDto>
{
    public async Task<WorkOrderDetailDto> Handle(ReleaseWorkOrderCommand cmd, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var wo = await db.LoadWorkOrderAsync(cmd.Id, ct);
        // Two defences against a double release (R5): this RowVersion (two requests at the same moment ->
        // the second UPDATE matches 0 rows -> 409) and Release() itself (a later request sees Released -> 422).
        db.ApplyRowVersion(wo, cmd.RowVersion);

        var item = await db.Items.AsNoTracking().SingleAsync(i => i.Id == wo.ItemId, ct);
        if (!item.IsActive) throw new DomainException($"Item '{item.Code}' is inactive; it cannot be released.");
        if (!await db.Lines.AnyAsync(l => l.Id == wo.LineId && l.IsActive, ct))
            throw new DomainException($"Line {wo.LineId} is inactive; move the order to another line first.");

        var recipe = await db.LoadRecipeAsync(item, now, ct);   // R1: approved BOM and routing in effect now
        wo.Release(recipe.BomVersion, recipe.RoutingVersion, recipe.Materials, recipe.Steps, now);   // R2

        // R3/R4: numbers are reserved only after every check passed, so a refused release wastes none.
        // Semi-finished items follow the finished good's serial and get none of their own (M1 1.2.1).
        if (item.IsSerialized)
        {
            var date = plant.DateOf(now);   // plant calendar day, not UTC (spec 1.5 #6)
            var numbers = await sequences.NextSerialNumbersAsync(wo.PlannedQty, ct);
            db.Serials.AddRange(numbers.Select(n => new Serial(SerialFormat.Build(item.Code, date, n), wo.Id, wo.ItemId)));
        }

        await db.SaveChangesAsync(ct);   // WO update (+RowVersion check), snapshot rows, serials, audit: one transaction
        return await db.GetWorkOrderDtoAsync(wo.Id, ct);
    }
}

// ---------- serials of a WO ----------

public sealed record GetWorkOrderSerialsQuery(int WorkOrderId, string? Status, int Page, int PageSize)
    : IRequest<PagedResult<SerialDto>>;

public sealed class GetWorkOrderSerialsValidator : AbstractValidator<GetWorkOrderSerialsQuery>
{
    public GetWorkOrderSerialsValidator()
    {
        RuleFor(x => x.Status).IsEnumName(typeof(SerialStatus)).When(x => !string.IsNullOrEmpty(x.Status))
            .WithMessage($"Status must be one of: {string.Join(", ", Enum.GetNames<SerialStatus>())}.");
        RuleFor(x => x.Page).GreaterThanOrEqualTo(1);
        RuleFor(x => x.PageSize).InclusiveBetween(1, SerialFilter.MaxPageSize);
    }
}

public sealed class GetWorkOrderSerialsHandler(IAppDbContext db) : IRequestHandler<GetWorkOrderSerialsQuery, PagedResult<SerialDto>>
{
    public async Task<PagedResult<SerialDto>> Handle(GetWorkOrderSerialsQuery q, CancellationToken ct)
    {
        if (!await db.WorkOrders.AnyAsync(w => w.Id == q.WorkOrderId, ct))
            throw new KeyNotFoundException($"Work order {q.WorkOrderId} not found.");

        var query = db.Serials.AsNoTracking().Where(s => s.WorkOrderId == q.WorkOrderId);
        if (!string.IsNullOrEmpty(q.Status))
        {
            var status = Enum.Parse<SerialStatus>(q.Status);
            query = query.Where(s => s.Status == status);
        }
        var total = await query.CountAsync(ct);
        // One WO's serials share model and date, so text order = sequence order (= label printing order).
        var page = await query.OrderBy(s => s.SerialNo)
            .Skip((q.Page - 1) * q.PageSize).Take(q.PageSize)
            .Select(s => new SerialDto(s.Id, s.SerialNo, s.Status.ToString(), s.CurrentSeq))
            .ToListAsync(ct);
        return new PagedResult<SerialDto>(page, q.Page, q.PageSize, total);
    }
}
