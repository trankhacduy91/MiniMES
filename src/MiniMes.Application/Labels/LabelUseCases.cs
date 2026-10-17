using FluentValidation;
using FluentValidation.Results;
using MediatR;
using Microsoft.EntityFrameworkCore;
using MiniMes.Application.Common;
using MiniMes.Contracts.Planning;
using MiniMes.Domain.Planning;

namespace MiniMes.Application.Labels;

internal static class LabelData
{
    public static async Task<(WorkOrder Wo, string ItemCode)> LoadPrintableAsync(this IAppDbContext db, int workOrderId, CancellationToken ct)
    {
        var wo = await db.WorkOrders.AsNoTracking().SingleOrDefaultAsync(w => w.Id == workOrderId, ct)
                 ?? throw new KeyNotFoundException($"Work order {workOrderId} not found.");
        wo.EnsureCanPrintLabels();   // Released or InProgress only -> 422
        var itemCode = await db.Items.Where(i => i.Id == wo.ItemId).Select(i => i.Code).SingleAsync(ct);
        return (wo, itemCode);
    }

    public static string Zpl(WorkOrder wo, string itemCode, string serialNo)
        => ZplLabelBuilder.Build(itemCode, wo.WoNo, serialNo, wo.BomVersion!.Value, wo.RoutingVersion!.Value);
}

// ---------- preview one label ----------

public sealed record PreviewLabelQuery(int WorkOrderId, string? SerialNo) : IRequest<byte[]>;

public sealed class PreviewLabelHandler(IAppDbContext db, ILabelPreview preview) : IRequestHandler<PreviewLabelQuery, byte[]>
{
    public async Task<byte[]> Handle(PreviewLabelQuery q, CancellationToken ct)
    {
        var (wo, itemCode) = await db.LoadPrintableAsync(q.WorkOrderId, ct);
        var serials = db.Serials.AsNoTracking().Where(s => s.WorkOrderId == wo.Id);
        var serialNo = await (q.SerialNo is null ? serials.OrderBy(s => s.SerialNo) : serials.Where(s => s.SerialNo == q.SerialNo))
                           .Select(s => s.SerialNo).FirstOrDefaultAsync(ct)
                       ?? throw new ValidationException([new ValidationFailure("SerialNo",
                           q.SerialNo is null ? $"Work order {wo.WoNo} has no serials." : $"Serial {q.SerialNo} is not in work order {wo.WoNo}.")]);
        return await preview.RenderPngAsync(LabelData.Zpl(wo, itemCode, serialNo), ct);   // one label: no print log
    }
}

// ---------- print (and reprint) ----------

public sealed record PrintLabelsCommand(int WorkOrderId, IReadOnlyList<string>? SerialNos, string? Reason)
    : IRequest<PrintLabelsResultDto>;

public sealed class PrintLabelsValidator : AbstractValidator<PrintLabelsCommand>
{
    public PrintLabelsValidator()
    {
        RuleFor(x => x.SerialNos).Must(s => s is null || (s.Count > 0 && s.Distinct().Count() == s.Count))
            .WithMessage("SerialNos must be omitted (all labels) or a non-empty list without duplicates.");
        RuleFor(x => x.Reason).MaximumLength(LabelPrintLog.ReasonMaxLength);
    }
}

// Rule R13: every printed label is logged; a serial printed before gets PrintNo + 1 and must have a reason.
// A unique index on (SerialId, PrintNo) makes two racing reprints of one serial a 409, never a shared number.
public sealed class PrintLabelsHandler(IAppDbContext db, ICurrentUser user, TimeProvider time)
    : IRequestHandler<PrintLabelsCommand, PrintLabelsResultDto>
{
    public async Task<PrintLabelsResultDto> Handle(PrintLabelsCommand cmd, CancellationToken ct)
    {
        var (wo, itemCode) = await db.LoadPrintableAsync(cmd.WorkOrderId, ct);

        var query = db.Serials.AsNoTracking().Where(s => s.WorkOrderId == wo.Id);
        query = cmd.SerialNos is null
            ? query.Where(s => s.Status != SerialStatus.Scrapped)
            : query.Where(s => cmd.SerialNos.Contains(s.SerialNo));
        var serials = await query.OrderBy(s => s.SerialNo).ToListAsync(ct);
        if (cmd.SerialNos is not null && serials.Count != cmd.SerialNos.Count)
        {
            var unknown = cmd.SerialNos.Except(serials.Select(s => s.SerialNo)).Take(5);
            throw new ValidationException([new ValidationFailure("SerialNos", $"Not serials of {wo.WoNo}: {string.Join(", ", unknown)}.")]);
        }
        if (serials.Count == 0) throw new ValidationException([new ValidationFailure("SerialNos", $"Work order {wo.WoNo} has no labels to print.")]);

        var ids = serials.Select(s => s.Id).ToList();
        var lastPrint = await db.LabelPrintLogs.Where(l => ids.Contains(l.SerialId))
            .GroupBy(l => l.SerialId).Select(g => new { SerialId = g.Key, Last = g.Max(l => l.PrintNo) })
            .ToDictionaryAsync(x => x.SerialId, x => x.Last, ct);

        var now = time.GetUtcNow().UtcDateTime;
        var printedBy = user.EmployeeCode ?? throw new InvalidOperationException("Printing needs a logged-in user.");
        var logs = serials.Select(s => new LabelPrintLog(s, lastPrint.GetValueOrDefault(s.Id) + 1, printedBy, cmd.Reason, now)).ToList();
        db.LabelPrintLogs.AddRange(logs);
        await db.SaveChangesAsync(ct);

        var zpl = string.Join(Environment.NewLine, serials.Select(s => LabelData.Zpl(wo, itemCode, s.SerialNo)));
        return new PrintLabelsResultDto(logs.Count, logs.Count(l => l.PrintNo > 1), zpl);
    }
}
