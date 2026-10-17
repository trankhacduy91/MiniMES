using FluentValidation;
using FluentValidation.Results;
using MediatR;
using Microsoft.EntityFrameworkCore;
using MiniMes.Application.Common;
using MiniMes.Contracts.Common;
using MiniMes.Contracts.Planning;
using MiniMes.Domain.MasterData.Items;
using MiniMes.Domain.Planning;

namespace MiniMes.Application.Planning;

// ---------- shared ----------

internal static class WorkOrderRules
{
    // A WO creates one serial per unit in one transaction at Release; this keeps that transaction sane.
    public const int MaxPlannedQty = 10_000;

    public static IRuleBuilderOptions<T, int> PlannedQtyRule<T>(this IRuleBuilder<T, int> rule)
        => rule.InclusiveBetween(1, MaxPlannedQty);

    // JSON without "Z" arrives as Unspecified: treat it as UTC (the API contract), not as server local time.
    public static DateTime AsUtc(DateTime value)
        => value.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(value, DateTimeKind.Utc) : value.ToUniversalTime();

    // Only made items get work orders; Raw material is bought. Inactive items take no new orders.
    public static async Task<Item> RequireOrderableItemAsync(this IAppDbContext db, int itemId, CancellationToken ct)
    {
        var item = await db.Items.AsNoTracking().SingleOrDefaultAsync(i => i.Id == itemId, ct);
        var error = item switch
        {
            null => $"Item {itemId} not found.",
            { IsActive: false } => $"Item '{item.Code}' is inactive.",
            { Type: ItemType.Raw } => $"Item '{item.Code}' is Raw material; work orders are for Semi or Finished items.",
            _ => null,
        };
        return error is null ? item! : throw new ValidationException([new ValidationFailure("ItemId", error)]);
    }

    public static async Task RequireActiveLineAsync(this IAppDbContext db, int lineId, CancellationToken ct)
    {
        var active = await db.Lines.Where(l => l.Id == lineId).Select(l => (bool?)l.IsActive).SingleOrDefaultAsync(ct);
        if (active is not true)
            throw new ValidationException([new ValidationFailure("LineId", active is null ? $"Line {lineId} not found." : $"Line {lineId} is inactive.")]);
    }
}

// ---------- preview ----------

public sealed record PreviewWorkOrderQuery(int ItemId, int PlannedQty) : IRequest<WorkOrderPreviewDto>;

public sealed class PreviewWorkOrderValidator : AbstractValidator<PreviewWorkOrderQuery>
{
    public PreviewWorkOrderValidator() => RuleFor(x => x.PlannedQty).PlannedQtyRule();
}

// Rule source = WorkOrder.Release itself: the preview releases a throw-away WO that is never saved,
// so anything Release would refuse (no routing step for a material...) shows up here first.
public sealed class PreviewWorkOrderHandler(IAppDbContext db, TimeProvider time) : IRequestHandler<PreviewWorkOrderQuery, WorkOrderPreviewDto>
{
    public async Task<WorkOrderPreviewDto> Handle(PreviewWorkOrderQuery q, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var item = await db.RequireOrderableItemAsync(q.ItemId, ct);
        var recipe = await db.LoadRecipeAsync(item, now, ct);

        var dryRun = WorkOrder.Create("PREVIEW", item.Id, lineId: 0, q.PlannedQty, now, now.AddHours(1));
        dryRun.Release(recipe.BomVersion, recipe.RoutingVersion, recipe.Materials, recipe.Steps, now);

        var (materials, operations) = await db.ToDtosAsync(
            dryRun.Materials.Select(m => (new MaterialSnapshot(m.ComponentItemId, m.OperationSeq, m.QtyPerUnit), m.RequiredQty, 0m, 0m)),
            recipe.Steps.Select(s => (s, 0, 0)), ct);
        return new WorkOrderPreviewDto(item.Id, item.Code, q.PlannedQty, recipe.BomVersion, recipe.RoutingVersion, now,
            materials, operations);
    }
}

// ---------- create ----------

public sealed record CreateWorkOrderCommand(int ItemId, int LineId, int PlannedQty, DateTime PlannedStart, DateTime PlannedEnd,
    int Priority, bool RequireFai) : IRequest<WorkOrderDetailDto>;

public sealed class CreateWorkOrderValidator : AbstractValidator<CreateWorkOrderCommand>
{
    public CreateWorkOrderValidator()
    {
        RuleFor(x => x.PlannedQty).PlannedQtyRule();
        RuleFor(x => x.PlannedEnd).GreaterThan(x => x.PlannedStart).WithMessage("PlannedEnd must be after PlannedStart.");
        RuleFor(x => x.Priority).InclusiveBetween(WorkOrder.MinPriority, WorkOrder.MaxPriority);
    }
}

// A Draft freezes nothing: no BOM/routing check here (that is the Release gate, rule R1), so a planner can
// prepare an order while engineering is still approving the BOM.
public sealed class CreateWorkOrderHandler(IAppDbContext db, ISequenceNumbers sequences, PlantTimeZone plant, TimeProvider time)
    : IRequestHandler<CreateWorkOrderCommand, WorkOrderDetailDto>
{
    public async Task<WorkOrderDetailDto> Handle(CreateWorkOrderCommand cmd, CancellationToken ct)
    {
        var item = await db.RequireOrderableItemAsync(cmd.ItemId, ct);
        await db.RequireActiveLineAsync(cmd.LineId, ct);

        var woNo = WorkOrderNumber.Format(plant.DateOf(time.GetUtcNow().UtcDateTime), await sequences.NextWorkOrderNumberAsync(ct));
        var wo = WorkOrder.Create(woNo, item.Id, cmd.LineId, cmd.PlannedQty,
            WorkOrderRules.AsUtc(cmd.PlannedStart), WorkOrderRules.AsUtc(cmd.PlannedEnd), cmd.Priority, cmd.RequireFai);
        db.WorkOrders.Add(wo);
        await db.SaveChangesAsync(ct);
        return await db.GetWorkOrderDtoAsync(wo.Id, ct);
    }
}

// ---------- update (Draft only) ----------

public sealed record UpdateWorkOrderCommand(int Id, int LineId, int PlannedQty, DateTime PlannedStart, DateTime PlannedEnd,
    bool RequireFai, string RowVersion) : IRequest<WorkOrderDetailDto>;

public sealed class UpdateWorkOrderValidator : AbstractValidator<UpdateWorkOrderCommand>
{
    public UpdateWorkOrderValidator()
    {
        RuleFor(x => x.PlannedQty).PlannedQtyRule();
        RuleFor(x => x.PlannedEnd).GreaterThan(x => x.PlannedStart).WithMessage("PlannedEnd must be after PlannedStart.");
        RuleFor(x => x.RowVersion).NotEmpty().Must(BeBase64).WithMessage("RowVersion must be the base64 value returned by the API.");
    }

    internal static bool BeBase64(string? value) => value is not null && Convert.TryFromBase64String(value, new byte[value.Length], out _);
}

public sealed class UpdateWorkOrderHandler(IAppDbContext db) : IRequestHandler<UpdateWorkOrderCommand, WorkOrderDetailDto>
{
    public async Task<WorkOrderDetailDto> Handle(UpdateWorkOrderCommand cmd, CancellationToken ct)
    {
        var wo = await db.LoadWorkOrderAsync(cmd.Id, ct);
        // The version the user saw, not the one just read: an edit made after they opened the screen -> 409.
        db.Entry(wo).Property(w => w.RowVersion).OriginalValue = Convert.FromBase64String(cmd.RowVersion);
        await db.RequireActiveLineAsync(cmd.LineId, ct);

        wo.UpdatePlan(cmd.LineId, cmd.PlannedQty, WorkOrderRules.AsUtc(cmd.PlannedStart), WorkOrderRules.AsUtc(cmd.PlannedEnd),
            cmd.RequireFai);   // Domain: Draft only (rule R6) -> 422
        await db.SaveChangesAsync(ct);
        return await db.GetWorkOrderDtoAsync(wo.Id, ct);
    }
}

// ---------- read ----------

public sealed record GetWorkOrderByIdQuery(int Id) : IRequest<WorkOrderDetailDto>;

public sealed class GetWorkOrderByIdHandler(IAppDbContext db) : IRequestHandler<GetWorkOrderByIdQuery, WorkOrderDetailDto>
{
    public Task<WorkOrderDetailDto> Handle(GetWorkOrderByIdQuery q, CancellationToken ct) => db.GetWorkOrderDtoAsync(q.Id, ct);
}

public sealed record GetWorkOrdersQuery(string? Status, int? LineId, string? Search, int Page, int PageSize)
    : IRequest<PagedResult<WorkOrderSummaryDto>>;

public sealed class GetWorkOrdersValidator : AbstractValidator<GetWorkOrdersQuery>
{
    public GetWorkOrdersValidator()
    {
        RuleFor(x => x.Status).IsEnumName(typeof(WorkOrderStatus)).When(x => !string.IsNullOrEmpty(x.Status))
            .WithMessage($"Status must be one of: {string.Join(", ", Enum.GetNames<WorkOrderStatus>())}.");
        RuleFor(x => x.Page).GreaterThanOrEqualTo(1);
        RuleFor(x => x.PageSize).InclusiveBetween(1, WorkOrderFilter.MaxPageSize);
    }
}

public sealed class GetWorkOrdersHandler(IAppDbContext db) : IRequestHandler<GetWorkOrdersQuery, PagedResult<WorkOrderSummaryDto>>
{
    public async Task<PagedResult<WorkOrderSummaryDto>> Handle(GetWorkOrdersQuery q, CancellationToken ct)
    {
        var query = from w in db.WorkOrders.AsNoTracking()
                    join i in db.Items on w.ItemId equals i.Id
                    join l in db.Lines on w.LineId equals l.Id
                    select new { w, ItemCode = i.Code, LineCode = l.Code };
        if (!string.IsNullOrEmpty(q.Status))
        {
            var status = Enum.Parse<WorkOrderStatus>(q.Status);
            query = query.Where(x => x.w.Status == status);
        }
        if (q.LineId is { } lineId) query = query.Where(x => x.w.LineId == lineId);
        if (!string.IsNullOrWhiteSpace(q.Search))
            query = query.Where(x => x.w.WoNo.Contains(q.Search) || x.ItemCode.Contains(q.Search));

        var total = await query.CountAsync(ct);
        // Same order as the dispatch list (spec v2): most urgent first, then by planned start.
        var page = await query.OrderBy(x => x.w.Priority).ThenBy(x => x.w.PlannedStart).ThenBy(x => x.w.WoNo)
            .Skip((q.Page - 1) * q.PageSize).Take(q.PageSize)
            .Select(x => new WorkOrderSummaryDto(x.w.Id, x.w.WoNo, x.w.ItemId, x.ItemCode, x.w.LineId, x.LineCode, x.w.PlannedQty,
                x.w.Operations.OrderByDescending(o => o.Seq).Select(o => o.QtyGood).FirstOrDefault(),
                x.w.PlannedStart, x.w.PlannedEnd, x.w.Status.ToString(), x.w.Priority,
                x.w.FaiStatus == null ? null : x.w.FaiStatus.ToString()))
            .ToListAsync(ct);
        return new PagedResult<WorkOrderSummaryDto>(page, q.Page, q.PageSize, total);
    }
}
