using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using MiniMes.Application.Common;
using MiniMes.Application.Labels;
using MiniMes.Contracts.Packing;
using MiniMes.Domain.Packing;
using MiniMes.Domain.Planning;

namespace MiniMes.Application.Packing;

// Spec M3b step 4: the carton and pallet labels (PackingLabelBuilder) and their print log (LabelPrintLog, one row
// per print, PrintNo per carton/pallet number). Called after the commit that closed the carton/pallet (spec 2.2
// trap 2: never print for a carton that may still roll back) and by the reprint endpoints.
public sealed class PackingLabels(IAppDbContext db, ICurrentUser user, PlantTimeZone plant, TimeProvider time)
{
    // automatic = printed because it just closed. A carton printed before was unpacked and refilled: its old label
    // shows the old content, so it is printed again with a reason the system gives (P11).
    public async Task<PackingLabelDto> PrintCartonAsync(string cartonNo, string? reason, bool automatic, CancellationToken ct)
    {
        var carton = await LoadCartonAsync(cartonNo, ct);
        var printNo = await NextPrintNoAsync(LabelType.Carton, carton.CartonNo, ct);
        if (automatic && printNo > 1) reason ??= $"Carton closed again with {carton.Qty} pcs.";
        var log = LabelPrintLog.ForCarton(carton, printNo, By(), reason, time.GetUtcNow().UtcDateTime);   // 422 if open
        var zpl = await CartonZplAsync(carton, ct);
        db.LabelPrintLogs.Add(log);
        await db.SaveChangesAsync(ct);   // two reprints at once: unique (type, key, PrintNo) -> 409 for one
        return new PackingLabelDto(carton.CartonNo, log.PrintNo, zpl);
    }

    public async Task<PackingLabelDto> PrintPalletAsync(string palletNo, string? reason, bool automatic, CancellationToken ct)
    {
        var pallet = await LoadPalletAsync(palletNo, ct);
        var printNo = await NextPrintNoAsync(LabelType.Pallet, pallet.PalletNo, ct);
        if (automatic && printNo > 1) reason ??= $"Pallet closed again with {pallet.CartonCount} cartons.";
        var log = LabelPrintLog.ForPallet(pallet, printNo, By(), reason, time.GetUtcNow().UtcDateTime);
        var zpl = await PalletZplAsync(pallet, ct);
        db.LabelPrintLogs.Add(log);
        await db.SaveChangesAsync(ct);
        return new PackingLabelDto(pallet.PalletNo, log.PrintNo, zpl);
    }

    // A preview is not a print: no log, and an open carton may be previewed (its quantity so far).
    public async Task<string> PreviewZplAsync(LabelType type, string key, CancellationToken ct) => type switch
    {
        LabelType.Carton => await CartonZplAsync(await LoadCartonAsync(key, ct), ct),
        LabelType.Pallet => await PalletZplAsync(await LoadPalletAsync(key, ct), ct),
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Serial labels are printed per work order (M2)."),
    };

    private async Task<string> CartonZplAsync(Carton carton, CancellationToken ct)
    {
        var info = await (from w in db.WorkOrders
                          join i in db.Items on w.ItemId equals i.Id
                          where w.Id == carton.WorkOrderId
                          select new { w.WoNo, i.Code }).SingleAsync(ct);
        return PackingLabelBuilder.Carton(carton.CartonNo, info.Code, carton.Qty, info.WoNo, LabelDate(carton.ClosedAt), carton.IsPartial);
    }

    private async Task<string> PalletZplAsync(Pallet pallet, CancellationToken ct)
    {
        var itemCode = await db.Items.Where(i => i.Id == pallet.ItemId).Select(i => i.Code).SingleAsync(ct);
        var totalQty = await db.Cartons.Where(c => c.PalletId == pallet.Id).SumAsync(c => c.Qty, ct);
        return PackingLabelBuilder.Pallet(pallet.PalletNo, itemCode, pallet.CartonCount, totalQty, LabelDate(pallet.ClosedAt), pallet.IsPartial);
    }

    // The packing date in plant time; an open one (preview) shows today.
    private DateOnly LabelDate(DateTime? closedAt) => plant.DateOf(closedAt ?? time.GetUtcNow().UtcDateTime);

    private async Task<Carton> LoadCartonAsync(string cartonNo, CancellationToken ct)
    {
        var key = cartonNo.Trim().ToUpperInvariant();
        return await db.Cartons.AsNoTracking().SingleOrDefaultAsync(c => c.CartonNo == key, ct)
               ?? throw new KeyNotFoundException($"Carton {key} not found.");
    }

    private async Task<Pallet> LoadPalletAsync(string palletNo, CancellationToken ct)
    {
        var key = palletNo.Trim().ToUpperInvariant();
        return await db.Pallets.AsNoTracking().SingleOrDefaultAsync(p => p.PalletNo == key, ct)
               ?? throw new KeyNotFoundException($"Pallet {key} not found.");
    }

    private async Task<int> NextPrintNoAsync(LabelType type, string key, CancellationToken ct)
        => (await db.LabelPrintLogs.Where(l => l.LabelType == type && l.TargetKey == key).MaxAsync(l => (int?)l.PrintNo, ct) ?? 0) + 1;

    private string By() => user.EmployeeCode ?? throw new InvalidOperationException("Printing needs a logged-in user.");
}

// ---------- POST /api/cartons/{no}/labels/print, /api/pallets/{no}/labels/print ----------

public sealed record PrintPackingLabelCommand(LabelType Type, string Key, string? Reason) : IRequest<PackingLabelDto>;

public sealed class PrintPackingLabelValidator : AbstractValidator<PrintPackingLabelCommand>
{
    public PrintPackingLabelValidator()
    {
        RuleFor(x => x.Type).Must(t => t is LabelType.Carton or LabelType.Pallet);
        RuleFor(x => x.Key).NotEmpty().MaximumLength(Carton.NumberMaxLength);
        RuleFor(x => x.Reason).MaximumLength(LabelPrintLog.ReasonMaxLength);
    }
}

public sealed class PrintPackingLabelHandler(PackingLabels labels) : IRequestHandler<PrintPackingLabelCommand, PackingLabelDto>
{
    public Task<PackingLabelDto> Handle(PrintPackingLabelCommand cmd, CancellationToken ct) => cmd.Type == LabelType.Carton
        ? labels.PrintCartonAsync(cmd.Key, cmd.Reason, automatic: false, ct)
        : labels.PrintPalletAsync(cmd.Key, cmd.Reason, automatic: false, ct);
}

// ---------- POST /api/cartons/{no}/labels/preview, /api/pallets/{no}/labels/preview ----------

public sealed record PreviewPackingLabelQuery(LabelType Type, string Key) : IRequest<byte[]>;

public sealed class PreviewPackingLabelHandler(PackingLabels labels, ILabelPreview preview) : IRequestHandler<PreviewPackingLabelQuery, byte[]>
{
    public async Task<byte[]> Handle(PreviewPackingLabelQuery q, CancellationToken ct)
        => await preview.RenderPngAsync(await labels.PreviewZplAsync(q.Type, q.Key, ct), PackingLabelBuilder.LabelaryInches, ct);
}
