using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MiniMes.Application.Common;
using MiniMes.Application.Execution;
using MiniMes.Application.Planning;
using MiniMes.Contracts.Packing;
using MiniMes.Domain.Common;
using MiniMes.Domain.Factory;
using MiniMes.Domain.Packing;

namespace MiniMes.Application.Packing;

// Spec M3b step 5, P7: the operator scans a carton label in Palletize mode; the carton goes onto the station's open
// pallet of its item (opened on the first carton). A full pallet closes itself and its label prints after the commit.
// Same pattern as the pack (2.3): the pallet's CartonCount and the carton's PalletId only move by conditional UPDATEs.
public sealed record PalletizeCommand(int StationId, string CartonNo) : IRequest<PalletizeResponse>;

public sealed class PalletizeValidator : AbstractValidator<PalletizeCommand>
{
    public PalletizeValidator() => RuleFor(x => x.CartonNo).NotEmpty().MaximumLength(Carton.NumberMaxLength);
}

public sealed class PalletizeHandler(IAppDbContext db, ISequenceNumbers sequences, PlantTimeZone plant, PackingLabels labels,
    TimeProvider time, ILogger<PalletizeHandler> log) : IRequestHandler<PalletizeCommand, PalletizeResponse>
{
    private const int MaxPalletTries = 3;

    public async Task<PalletizeResponse> Handle(PalletizeCommand cmd, CancellationToken ct)
    {
        var station = await db.LoadStationAsync(cmd.StationId, ct);   // 404
        if (station.Type != StationType.Pack) throw new DomainException($"Station {station.Code} is not a Pack station.");
        var cartonNo = PackingReads.Key(cmd.CartonNo);

        PalletizeResponse response;
        await using (var tx = await db.BeginTransactionAsync(ct))
        {
            response = await PalletizeAsync(station.Id, cartonNo, ct);
            if (response.Result == "Ok" && !response.Duplicate) await tx.CommitAsync(ct);   // anything else wrote nothing
        }
        if (response.PalletClosed) response = await WithLabelAsync(response, ct);

        log.LogInformation("Palletize {CartonNo} at station {StationId}: {PalletizeResult} {ReasonCode} pallet {PalletNo} {CartonCount}/{CartonsPerPallet} closed={PalletClosed}",
            cartonNo, station.Id, response.Result, response.ReasonCode, response.PalletNo, response.CartonCount, response.CartonsPerPallet, response.PalletClosed);
        return response;
    }

    private async Task<PalletizeResponse> PalletizeAsync(int stationId, string cartonNo, CancellationToken ct)
    {
        // Spec 2.5: the kiosk has one scan box; a serial or a pallet label scanned here is told apart by its shape.
        if (!PackingNumberFormat.IsCartonNo(cartonNo))
            return Blocked("NotACartonLabel", $"{cartonNo} is not a carton label.", 0);
        var carton = await db.Cartons.AsNoTracking().SingleOrDefaultAsync(c => c.CartonNo == cartonNo, ct);
        if (carton is null) return Blocked("CartonNotFound", $"Carton {cartonNo} not found.", 0);
        var perPallet = await db.PackingSpecs.Where(p => p.ItemId == carton.ItemId).Select(p => (int?)p.CartonsPerPallet).SingleOrDefaultAsync(ct)
                        ?? throw new DomainException($"No packing spec for the item of carton {cartonNo}.");

        // Scanned again: it is on this station's open pallet already. Show that pallet, write nothing.
        if (carton.PalletId is { } onPallet && await OpenPalletQuery(stationId, carton.ItemId).SingleOrDefaultAsync(ct) is { } open
            && open.Id == onPallet)
            return new PalletizeResponse("Ok", $"Carton {cartonNo} is already on pallet {open.PalletNo} ({open.CartonCount}/{perPallet}).",
                null, true, open.PalletNo, open.CartonCount, perPallet, false);

        var onHold = await db.IsCartonOnHoldAsync(carton.Id, ct);
        var now = time.GetUtcNow().UtcDateTime;
        for (var attempt = 0; attempt < MaxPalletTries; attempt++)
        {
            var pallet = await OpenPalletAsync(stationId, carton.ItemId, now, ct);
            if (pallet.CheckAdd(carton, onHold, perPallet) is { } block)
            {
                // Full but still Open: the spec was lowered. Close it, take a new one.
                if (block == PalletizeBlock.PalletFull) { await CloseIfFullAsync(pallet.Id, perPallet, now, ct); continue; }
                return Blocked(block.ToString(), Message(block, cartonNo), perPallet);
            }

            var added = await db.Pallets
                .Where(p => p.Id == pallet.Id && p.Status == PalletStatus.Open && p.CartonCount < perPallet)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.CartonCount, p => p.CartonCount + 1), ct) == 1;
            if (!added) continue;   // another carton filled it meanwhile: read again
            // The carton side, conditional too: two stations scanning the same carton at once put it on one pallet.
            var moved = await db.Cartons
                .Where(c => c.Id == carton.Id && c.Status == CartonStatus.Closed && c.PalletId == null)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.PalletId, (int?)pallet.Id), ct) == 1;
            if (!moved) return Blocked(nameof(PalletizeBlock.CartonOnAPallet), Message(PalletizeBlock.CartonOnAPallet, cartonNo), perPallet);

            var closed = await CloseIfFullAsync(pallet.Id, perPallet, now, ct);
            var count = await db.Pallets.Where(p => p.Id == pallet.Id).Select(p => p.CartonCount).SingleAsync(ct);
            var message = closed ? $"Pallet {pallet.PalletNo} full ({count}/{perPallet}): closed." : $"Carton on pallet {pallet.PalletNo} ({count}/{perPallet}).";
            return new PalletizeResponse("Ok", message, null, false, pallet.PalletNo, count, perPallet, closed);
        }
        throw new ConflictException("The pallet is busy: scan the carton again.");
    }

    private static string Message(PalletizeBlock block, string cartonNo) => block switch
    {
        PalletizeBlock.CartonNotClosed => $"Carton {cartonNo} is not closed yet.",
        PalletizeBlock.CartonOnAPallet => $"Carton {cartonNo} is on a pallet already.",
        PalletizeBlock.CartonOnHold => $"Carton {cartonNo} contains held serials.",
        PalletizeBlock.WrongItem => $"Carton {cartonNo} is another item than the open pallet.",
        _ => $"Carton {cartonNo} cannot go on the pallet ({block}).",
    };

    private async Task<PalletizeResponse> WithLabelAsync(PalletizeResponse response, CancellationToken ct)
    {
        try
        {
            var label = await labels.PrintPalletAsync(response.PalletNo!, reason: null, automatic: true, ct);
            return response with { LabelZpl = label.Zpl };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Pallet {PalletNo} closed but its label was not made", response.PalletNo);
            return response with { Message = response.Message + " Label NOT printed: print it again." };
        }
    }

    // The station's Open pallet of this item, or a new one. Same race handling as the carton (unique filtered index).
    private async Task<Pallet> OpenPalletAsync(int stationId, int itemId, DateTime now, CancellationToken ct)
    {
        var open = await OpenPalletQuery(stationId, itemId).SingleOrDefaultAsync(ct);
        if (open is not null) return open;

        var pallet = new Pallet(PackingNumberFormat.Pallet(plant.DateOf(now), await sequences.NextPalletNumberAsync(ct)), itemId, stationId, now);
        db.Pallets.Add(pallet);
        try
        {
            await db.SaveChangesAsync(ct);
            return pallet;
        }
        catch (ConflictException)
        {
            return await OpenPalletQuery(stationId, itemId).SingleAsync(ct);
        }
        finally
        {
            db.Entry(pallet).State = EntityState.Detached;   // CartonCount and Status change by ExecuteUpdate from here on
        }
    }

    private IQueryable<Pallet> OpenPalletQuery(int stationId, int itemId)
        => db.Pallets.AsNoTracking().Where(p => p.StationId == stationId && p.ItemId == itemId && p.Status == PalletStatus.Open);

    private async Task<bool> CloseIfFullAsync(int palletId, int perPallet, DateTime now, CancellationToken ct)
        => await db.Pallets
            .Where(p => p.Id == palletId && p.Status == PalletStatus.Open && p.CartonCount >= perPallet)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, PalletStatus.Closed).SetProperty(p => p.ClosedAt, (DateTime?)now), ct) == 1;

    private static PalletizeResponse Blocked(string reasonCode, string message, int perPallet)
        => new("Blocked", message, reasonCode, false, null, 0, perPallet, false);
}
