using ClosedXML.Excel;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MiniMes.Application.Common;
using MiniMes.Contracts.Traceability;
using MiniMes.Domain.Material;

namespace MiniMes.Application.Traceability;

// Spec M6 step 8: the trace screen's use cases (QC, Supervisor, Admin). The SQL is in ITraceQueries (step 7).

// ---------- GET /api/trace/forward(.xlsx): from a lot, or from a supplier lot of an item ----------

public sealed record GetForwardTraceQuery(string? LotNo, string? SupplierLot, string? ItemCode) : IRequest<ForwardTraceResultDto>;

public sealed record ExportForwardTraceQuery(string? LotNo, string? SupplierLot, string? ItemCode) : IRequest<byte[]>;

internal static class ForwardTraceRules
{
    public static void Apply<T>(AbstractValidator<T> v, Func<T, string?> lot, Func<T, string?> supplierLot, Func<T, string?> item)
    {
        v.RuleFor(x => x).Must(x => string.IsNullOrWhiteSpace(lot(x)) != string.IsNullOrWhiteSpace(supplierLot(x)))
            .OverridePropertyName("LotNo").WithMessage("Give a lot number, or a supplier lot with its item code.");
        v.RuleFor(x => item(x)).NotEmpty().When(x => !string.IsNullOrWhiteSpace(supplierLot(x)))
            .OverridePropertyName("ItemCode").WithMessage("A supplier lot needs the item code (the same code can exist for two items).");
    }

    // Display order of the summary: the further the boards went, the more urgent (spec 2.2b).
    public static readonly string[] LocationOrder = ["Shipped", "OnPallet", "InCarton", "CompletedUnpacked", "Wip", "Scrapped"];

    public static async Task<ForwardTraceResultDto> RunAsync(IAppDbContext db, ITraceQueries trace, string? lotNo, string? supplierLot,
        string? itemCode, CancellationToken ct)
    {
        IReadOnlyList<ForwardTraceRow> rows;
        List<string> start;
        if (!string.IsNullOrWhiteSpace(lotNo))
        {
            lotNo = lotNo.Trim().ToUpperInvariant();
            rows = await trace.ForwardByLotAsync(lotNo, ct);
            start = await db.MaterialLots.AsNoTracking().Where(l => l.LotNo == lotNo).Select(l => l.LotNo).ToListAsync(ct);
        }
        else
        {
            supplierLot = supplierLot!.Trim();
            var code = itemCode!.Trim().ToUpperInvariant();
            // Unknown item or supplier lot: an empty answer, like an unknown lot (a search, not a lookup).
            var itemId = await db.Items.AsNoTracking().Where(i => i.Code == code).Select(i => (int?)i.Id).SingleOrDefaultAsync(ct);
            rows = itemId is { } id ? await trace.ForwardBySupplierLotAsync(supplierLot, id, ct) : [];
            start = await db.MaterialLots.AsNoTracking().Where(l => l.SupplierLot == supplierLot && l.ItemId == itemId)
                .OrderBy(l => l.LotNo).Select(l => l.LotNo).ToListAsync(ct);
        }

        var byLocation = rows.GroupBy(r => r.Location)
            .OrderBy(g => Array.IndexOf(LocationOrder, g.Key) is var i and >= 0 ? i : int.MaxValue)
            .Select(g => new LocationCountDto(g.Key, g.Count())).ToList();
        var serials = rows.Select(r => new ForwardTraceDto(r.SerialNo, r.SerialStatus, r.WoNo, r.Depth, r.LotNo, r.StationCode,
            r.ConsumedAt, r.OperatorCode, r.Location, r.CartonNo, r.PalletNo, r.ShipmentRef)).ToList();
        return new ForwardTraceResultDto(start, byLocation, serials);
    }
}

public sealed class GetForwardTraceValidator : AbstractValidator<GetForwardTraceQuery>
{
    public GetForwardTraceValidator() => ForwardTraceRules.Apply(this, x => x.LotNo, x => x.SupplierLot, x => x.ItemCode);
}

public sealed class ExportForwardTraceValidator : AbstractValidator<ExportForwardTraceQuery>
{
    public ExportForwardTraceValidator() => ForwardTraceRules.Apply(this, x => x.LotNo, x => x.SupplierLot, x => x.ItemCode);
}

public sealed class GetForwardTraceHandler(IAppDbContext db, ITraceQueries trace) : IRequestHandler<GetForwardTraceQuery, ForwardTraceResultDto>
{
    public Task<ForwardTraceResultDto> Handle(GetForwardTraceQuery q, CancellationToken ct)
        => ForwardTraceRules.RunAsync(db, trace, q.LotNo, q.SupplierLot, q.ItemCode, ct);
}

public sealed class ExportForwardTraceHandler(IAppDbContext db, ITraceQueries trace, PlantTimeZone plant, TimeProvider time)
    : IRequestHandler<ExportForwardTraceQuery, byte[]>
{
    public async Task<byte[]> Handle(ExportForwardTraceQuery q, CancellationToken ct)
    {
        var result = await ForwardTraceRules.RunAsync(db, trace, q.LotNo, q.SupplierLot, q.ItemCode, ct);
        var from = string.IsNullOrWhiteSpace(q.LotNo) ? $"supplier lot {q.SupplierLot!.Trim()} of {q.ItemCode!.Trim().ToUpperInvariant()}"
            : $"lot {q.LotNo.Trim().ToUpperInvariant()}";
        return TraceWorkbook.Forward(result, from, plant, time.GetUtcNow().UtcDateTime);
    }
}

// The file sent to a customer or kept for an audit: one row per serial, times in plant time, a summary sheet.
public static class TraceWorkbook
{
    private const string DateTimeFormat = "yyyy-mm-dd hh:mm:ss";

    public static readonly string[] Columns = ["Serial", "Serial status", "Work order", "Lot used", "Depth", "Station", "Consumed at",
        "Operator", "Location", "Carton", "Pallet", "Shipment ref"];

    public static byte[] Forward(ForwardTraceResultDto result, string from, PlantTimeZone plant, DateTime nowUtc)
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Serials");
        ws.Cell(1, 1).Value = $"Forward trace from {from}: {result.Serials.Count} serial(s). Start lots: {string.Join(", ", result.StartLots)}. " +
                              $"Times in plant time ({plant.Zone.Id}), exported {Local(nowUtc, plant):yyyy-MM-dd HH:mm}.";
        ws.Cell(1, 1).Style.Font.Bold = true;
        for (var i = 0; i < Columns.Length; i++) ws.Cell(3, i + 1).Value = Columns[i];
        ws.Row(3).Style.Font.Bold = true;
        ws.SheetView.FreezeRows(3);
        var r = 4;
        foreach (var s in result.Serials)
        {
            ws.Cell(r, 1).Value = s.SerialNo;
            ws.Cell(r, 2).Value = s.SerialStatus;
            ws.Cell(r, 3).Value = s.WoNo;
            ws.Cell(r, 4).Value = s.LotNo;
            ws.Cell(r, 5).Value = s.Depth;
            ws.Cell(r, 6).Value = s.StationCode;
            ws.Cell(r, 7).Value = Local(s.ConsumedAt, plant);
            ws.Cell(r, 7).Style.NumberFormat.Format = DateTimeFormat;
            ws.Cell(r, 8).Value = s.OperatorCode;
            ws.Cell(r, 9).Value = s.Location;
            ws.Cell(r, 10).Value = s.CartonNo;
            ws.Cell(r, 11).Value = s.PalletNo;
            ws.Cell(r, 12).Value = s.ShipmentRef;
            r++;
        }
        ws.Columns(1, Columns.Length).Width = 18;   // fixed: AdjustToContents needs system fonts, missing on the Linux CI runner

        var summary = wb.AddWorksheet("By location");
        summary.Cell(1, 1).Value = "Location";
        summary.Cell(1, 2).Value = "Serials";
        summary.Row(1).Style.Font.Bold = true;
        for (var i = 0; i < result.ByLocation.Count; i++)
        {
            summary.Cell(i + 2, 1).Value = result.ByLocation[i].Location;
            summary.Cell(i + 2, 2).Value = result.ByLocation[i].Count;
        }
        summary.Columns(1, 2).Width = 20;

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    private static DateTime Local(DateTime utc, PlantTimeZone plant)
        => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), plant.Zone);
}

// ---------- GET /api/trace/backward?serialNo= : the whole history of a board, flat ----------

public sealed record GetBackwardTraceQuery(string SerialNo) : IRequest<IReadOnlyList<BackwardTraceDto>>;

public sealed class GetBackwardTraceValidator : AbstractValidator<GetBackwardTraceQuery>
{
    public GetBackwardTraceValidator() => RuleFor(x => x.SerialNo).NotEmpty().MaximumLength(30);
}

public sealed class GetBackwardTraceHandler(ITraceQueries trace) : IRequestHandler<GetBackwardTraceQuery, IReadOnlyList<BackwardTraceDto>>
{
    public async Task<IReadOnlyList<BackwardTraceDto>> Handle(GetBackwardTraceQuery q, CancellationToken ct)
        => (await trace.BackwardAsync(q.SerialNo.Trim().ToUpperInvariant(), ct))
            .Select(r => new BackwardTraceDto(r.Depth, r.LotNo, r.ChildLotNo, r.ItemCode, r.SupplierLot, r.StationCode, r.ConsumedAt,
                r.OperatorCode, r.LoadedBy, r.FromRepair)).ToList();
}

// ---------- GET /api/trace/nodes/{kind}/{key}/children : one level of the backward tree (lazy, spec 2.6) ----------

public sealed record GetTraceChildrenQuery(string Kind, string Key) : IRequest<IReadOnlyList<TraceNodeDto>>;

public sealed class GetTraceChildrenValidator : AbstractValidator<GetTraceChildrenQuery>
{
    public GetTraceChildrenValidator()
    {
        RuleFor(x => x.Kind).Must(k => k is TraceNodeKind.Serial or TraceNodeKind.Lot).WithMessage("Kind must be serial or lot.");
        RuleFor(x => x.Key).NotEmpty().MaximumLength(50);
    }
}

public sealed class GetTraceChildrenHandler(IAppDbContext db, ITraceQueries trace) : IRequestHandler<GetTraceChildrenQuery, IReadOnlyList<TraceNodeDto>>
{
    public async Task<IReadOnlyList<TraceNodeDto>> Handle(GetTraceChildrenQuery q, CancellationToken ct)
    {
        var key = q.Key.Trim().ToUpperInvariant();
        if (q.Kind == TraceNodeKind.Serial)
        {
            // The lots the board consumed; a lot has children when it was split from another (an ancestor row).
            var rows = await trace.BackwardAsync(key, ct);
            return rows.Where(r => r.Depth == 0)
                .Select(r => new TraceNodeDto(TraceNodeKind.Lot, r.LotNo, r.ItemCode, r.SupplierLot, r.StationCode, r.ConsumedAt,
                    r.OperatorCode, r.LoadedBy, null, rows.Any(a => a.ChildLotNo == r.LotNo), r.FromRepair))
                .ToList();
        }

        // One genealogy level up: the lots this one was made from, with the quantity taken from each.
        return await (from child in db.MaterialLots
                      join g in db.LotGenealogy on child.Id equals g.ChildLotId
                      join parent in db.MaterialLots on g.ParentLotId equals parent.Id
                      join i in db.Items on parent.ItemId equals i.Id
                      where child.LotNo == key
                      orderby parent.LotNo
                      select new TraceNodeDto(TraceNodeKind.Lot, parent.LotNo, i.Code, parent.SupplierLot, null, null, null, null, g.Qty,
                          db.LotGenealogy.Any(x => x.ChildLotId == parent.Id)))
            .AsNoTracking().ToListAsync(ct);
    }
}
