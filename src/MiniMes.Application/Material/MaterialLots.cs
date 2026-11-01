using FluentValidation;
using FluentValidation.Results;
using MediatR;
using Microsoft.EntityFrameworkCore;
using MiniMes.Application.Common;
using MiniMes.Application.Labels;
using MiniMes.Application.Planning;
using MiniMes.Contracts.Common;
using MiniMes.Contracts.Material;
using MiniMes.Domain.Material;
using MiniMes.Domain.MasterData.Items;

namespace MiniMes.Application.Material;

// Member init, not a constructor: EF translates x.Lot.LotNo through an initializer, not through constructor arguments.
internal sealed class LotWithItem
{
    public required MaterialLot Lot { get; init; }
    public required Item Item { get; init; }
}

internal static class MaterialLotMapping
{
    // Filter and sort on this (entity columns), project with ToDtos at the very end: EF cannot translate a Where/OrderBy
    // on the properties of a DTO built by a record constructor.
    public static IQueryable<LotWithItem> WithItem(this IAppDbContext db, IQueryable<MaterialLot> lots)
        => from l in lots
           join i in db.Items on l.ItemId equals i.Id
           select new LotWithItem { Lot = l, Item = i };

    public static IQueryable<MaterialLotDto> ToDtos(this IQueryable<LotWithItem> rows)
        => rows.Select(x => new MaterialLotDto(x.Lot.Id, x.Lot.LotNo, x.Lot.ItemId, x.Item.Code, x.Item.Name, x.Item.Uom,
            x.Lot.Qty, x.Lot.RemainingQty, x.Lot.SupplierLot, x.Lot.ReceivedAt, x.Lot.ExpiryAt, x.Lot.Status.ToString()));

    // Plant-time date printed on the label. The lot is blocked from the first minute of that day (R3).
    public static DateOnly? ExpiryDate(this MaterialLotDto lot, PlantTimeZone plant)
        => lot.ExpiryAt is { } e ? plant.DateOf(e) : null;

    public static string Label(this MaterialLotDto lot, PlantTimeZone plant)
        => LotLabelBuilder.Build(lot.LotNo, lot.ItemCode, lot.SupplierLot, lot.Qty, lot.Uom, lot.ExpiryDate(plant),
            lot.Status == nameof(MaterialLotStatus.PendingIqc));
}

// ---------- POST /api/material/lots: receive N reels of one supplier delivery ----------

public sealed record ReceiveLotsCommand(int ItemId, string SupplierLot, decimal QtyPerReel, int ReelCount, DateOnly? ExpiryDate)
    : IRequest<ReceiveLotsResultDto>;

public sealed class ReceiveLotsValidator : AbstractValidator<ReceiveLotsCommand>
{
    public const int MaxReels = 100;   // one delivery; more than that is a typo (10,000 typed in the reel box)

    public ReceiveLotsValidator()
    {
        RuleFor(x => x.SupplierLot).NotEmpty().MaximumLength(MaterialLot.SupplierLotMaxLength)
            .Must(s => s is null || s.All(c => c is >= ' ' and < '~' and not '^'))
            .WithMessage("Supplier lot must be printable ASCII without '^' or '~' (it is printed on the label).");
        RuleFor(x => x.QtyPerReel).GreaterThan(0).LessThanOrEqualTo(10_000_000m);
        RuleFor(x => x.ReelCount).InclusiveBetween(1, MaxReels);
    }
}

// Spec M6 step 2: numbers from a SQL sequence (LotNumberFormat), all reels in one SaveChanges, and the labels.
// [v2] R15: lots wait for IQC unless the item is exempt (SkipIqc, e.g. packing bags).
public sealed class ReceiveLotsHandler(IAppDbContext db, ISequenceNumbers sequences, PlantTimeZone plant, TimeProvider time)
    : IRequestHandler<ReceiveLotsCommand, ReceiveLotsResultDto>
{
    public async Task<ReceiveLotsResultDto> Handle(ReceiveLotsCommand cmd, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var item = await db.Items.AsNoTracking().SingleOrDefaultAsync(i => i.Id == cmd.ItemId, ct)
                   ?? throw Invalid(nameof(cmd.ItemId), $"Item {cmd.ItemId} does not exist.");
        if (!item.IsActive) throw Invalid(nameof(cmd.ItemId), $"Item '{item.Code}' is inactive.");
        if (!item.IsLotControlled)
            throw Invalid(nameof(cmd.ItemId), $"Item '{item.Code}' is not lot-controlled: it is received without lots.");
        if (Units.IsDiscrete(item.Uom) && cmd.QtyPerReel != decimal.Truncate(cmd.QtyPerReel))
            throw Invalid(nameof(cmd.QtyPerReel), $"'{item.Code}' is counted in {item.Uom}: the quantity must be a whole number.");

        var today = plant.DateOf(now);
        DateTime? expiryAt = null;
        if (cmd.ExpiryDate is { } expiry)
        {
            if (expiry <= today) throw Invalid(nameof(cmd.ExpiryDate), $"Expiry {expiry:yyyy-MM-dd} must be after today ({today:yyyy-MM-dd}).");
            expiryAt = TimeZoneInfo.ConvertTimeToUtc(expiry.ToDateTime(TimeOnly.MinValue), plant.Zone);
        }

        var numbers = await sequences.NextLotNumbersAsync(cmd.ReelCount, ct);   // after the checks: a refusal wastes none
        var lots = numbers.Select(n => new MaterialLot(LotNumberFormat.Build(today, n), item.Id, cmd.QtyPerReel,
            cmd.SupplierLot, now, expiryAt, needsIqc: !item.SkipIqc)).ToList();
        db.MaterialLots.AddRange(lots);
        await db.SaveChangesAsync(ct);   // all reels or none

        var ids = lots.Select(l => l.Id).ToList();
        var dtos = await db.WithItem(db.MaterialLots.AsNoTracking().Where(l => ids.Contains(l.Id))).OrderBy(x => x.Lot.LotNo).ToDtos().ToListAsync(ct);
        return new ReceiveLotsResultDto(dtos, string.Join(Environment.NewLine, dtos.Select(l => l.Label(plant))));
    }

    private static ValidationException Invalid(string property, string message) => new([new ValidationFailure(property, message)]);
}

// ---------- GET /api/material/lots ----------

public sealed record GetMaterialLotsQuery(int? ItemId, string? Status, string? Search, int Page, int PageSize)
    : IRequest<PagedResult<MaterialLotDto>>;

public sealed class GetMaterialLotsValidator : AbstractValidator<GetMaterialLotsQuery>
{
    public GetMaterialLotsValidator()
    {
        RuleFor(x => x.Status).IsEnumName(typeof(MaterialLotStatus)).When(x => !string.IsNullOrEmpty(x.Status))
            .WithMessage($"Status must be one of: {string.Join(", ", Enum.GetNames<MaterialLotStatus>())}.");
        RuleFor(x => x.Page).GreaterThanOrEqualTo(1);
        RuleFor(x => x.PageSize).InclusiveBetween(1, MaterialLotFilter.MaxPageSize);
    }
}

public sealed class GetMaterialLotsHandler(IAppDbContext db) : IRequestHandler<GetMaterialLotsQuery, PagedResult<MaterialLotDto>>
{
    public async Task<PagedResult<MaterialLotDto>> Handle(GetMaterialLotsQuery q, CancellationToken ct)
    {
        var lots = db.MaterialLots.AsNoTracking();
        if (q.ItemId is { } itemId) lots = lots.Where(l => l.ItemId == itemId);
        if (!string.IsNullOrEmpty(q.Status))
        {
            var status = Enum.Parse<MaterialLotStatus>(q.Status);
            lots = lots.Where(l => l.Status == status);
        }
        var query = db.WithItem(lots);
        if (!string.IsNullOrWhiteSpace(q.Search))
            query = query.Where(x => x.Lot.LotNo.Contains(q.Search) || (x.Lot.SupplierLot != null && x.Lot.SupplierLot.Contains(q.Search))
                                     || x.Item.Code.Contains(q.Search));

        var total = await query.CountAsync(ct);
        var page = await query.OrderByDescending(x => x.Lot.ReceivedAt).ThenByDescending(x => x.Lot.LotNo)   // newest delivery first
            .Skip((q.Page - 1) * q.PageSize).Take(q.PageSize).ToDtos().ToListAsync(ct);
        return new PagedResult<MaterialLotDto>(page, q.Page, q.PageSize, total);
    }
}

// ---------- label of one lot: ZPL to reprint, PNG to look at ----------

public sealed record GetLotLabelQuery(int LotId) : IRequest<string>;

public sealed class GetLotLabelHandler(IAppDbContext db, PlantTimeZone plant) : IRequestHandler<GetLotLabelQuery, string>
{
    public async Task<string> Handle(GetLotLabelQuery q, CancellationToken ct)
        => (await LoadAsync(db, q.LotId, ct)).Label(plant);

    internal static async Task<MaterialLotDto> LoadAsync(IAppDbContext db, int lotId, CancellationToken ct)
        => await db.WithItem(db.MaterialLots.AsNoTracking().Where(l => l.Id == lotId)).ToDtos().SingleOrDefaultAsync(ct)
           ?? throw new KeyNotFoundException($"Lot {lotId} not found.");
}

// One label through Labelary (ILabelPreview), like the product label of M2.
public sealed record PreviewLotLabelQuery(int LotId) : IRequest<byte[]>;

public sealed class PreviewLotLabelHandler(IAppDbContext db, PlantTimeZone plant, ILabelPreview preview)
    : IRequestHandler<PreviewLotLabelQuery, byte[]>
{
    public async Task<byte[]> Handle(PreviewLotLabelQuery q, CancellationToken ct)
        => await preview.RenderPngAsync((await GetLotLabelHandler.LoadAsync(db, q.LotId, ct)).Label(plant), ct);
}
