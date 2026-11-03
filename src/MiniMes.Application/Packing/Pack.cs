using System.Diagnostics;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using MiniMes.Application.Common;
using MiniMes.Application.Execution;
using MiniMes.Application.Planning;
using MiniMes.Contracts.Execution;
using MiniMes.Contracts.Packing;
using MiniMes.Domain.Common;
using MiniMes.Domain.Execution;
using MiniMes.Domain.Factory;
using MiniMes.Domain.Packing;
using MiniMes.Domain.Planning;

namespace MiniMes.Application.Packing;

// Spec M3b 2.2 + 2.3: pack = scan PACK through the M3 route check + put the board in the station's open carton, in
// ONE transaction (P1). A full carton closes itself (P5). Blocked answers are 200 + reasonCode and write nothing.
public sealed record PackCommand(int StationId, string SerialNo) : IRequest<PackResponse>;

public sealed class PackValidator : AbstractValidator<PackCommand>
{
    public PackValidator() => RuleFor(x => x.SerialNo).NotEmpty().MaximumLength(Serial.SerialNoMaxLength);
}

public sealed class PackHandler(IAppDbContext db, ScanExecutor scans, ISequenceNumbers sequences, PlantTimeZone plant,
    PackingLabels labels, ICurrentUser user, TimeProvider time, ILogger<PackHandler> log)
    : IRequestHandler<PackCommand, PackResponse>
{
    // Losing the race for a carton (filled or opened by another pack meanwhile) means: read again and retry.
    // More than this many times in a row is not a race any more.
    private const int MaxCartonTries = 3;

    private sealed record Board(long Id, int WorkOrderId, int ItemId, string ItemCode, SerialStatus Status);

    public async Task<PackResponse> Handle(PackCommand cmd, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        var serialNo = ScanExecutor.Normalize(cmd.SerialNo);
        var station = await db.LoadStationAsync(cmd.StationId, ct);   // 404
        if (station.Type != StationType.Pack) throw new DomainException($"Station {station.Code} is not a Pack station.");

        PackResponse response;
        await using (var tx = await db.BeginTransactionAsync(ct))
        {
            response = await PackAsync(tx, station.Id, serialNo, ct);
            if (response.Result == "Ok" && !response.Duplicate) await tx.CommitAsync(ct);   // anything else wrote nothing
        }
        if (response.CartonClosed) response = await WithLabelAsync(response, ct);

        log.LogInformation("Pack {SerialNo} at station {StationId}: {PackResult} {ReasonCode} carton {CartonNo} {CartonQty}/{QtyPerCarton} closed={CartonClosed} duplicate={Duplicate} repacked={Repacked} in {ElapsedMs} ms",
            serialNo, station.Id, response.Result, response.ReasonCode, response.CartonNo, response.CartonQty, response.QtyPerCarton,
            response.CartonClosed, response.Duplicate, response.Repacked, watch.ElapsedMilliseconds);
        return response;
    }

    private async Task<PackResponse> PackAsync(IDbContextTransaction tx, int stationId, string serialNo, CancellationToken ct)
    {
        var board = await (from s in db.Serials.AsNoTracking()
                           join i in db.Items on s.ItemId equals i.Id
                           where s.SerialNo == serialNo
                           select new Board(s.Id, s.WorkOrderId, s.ItemId, i.Code, s.Status)).SingleOrDefaultAsync(ct);
        // Unknown serial: no spec to look up, the scan answers SerialNotFound.
        var qtyPerCarton = 0;
        if (board is not null)
            qtyPerCarton = await db.PackingSpecs.Where(p => p.ItemId == board.ItemId).Select(p => (int?)p.QtyPerCarton).SingleOrDefaultAsync(ct)
                           ?? throw new DomainException($"No packing spec for item {board.ItemCode}.");

        // The board has passed PACK already: it is in a carton (scanned again: show that carton, spec 2.2 trap 1), or
        // it was unpacked (P9) and goes into a carton again without a second PACK scan (user decision, M3b step 3).
        if (board is { Status: SerialStatus.Completed })
        {
            if (await LiveCartonAsync(board.Id, ct) is { } current) return AlreadyIn(current, qtyPerCarton);
            if (await db.OpenSessionWoIdAsync(stationId, ct) is not { } sessionWoId)
                return Blocked(nameof(BlockReason.NoOpenSession), "No operator session is open at this station.", qtyPerCarton);
            if (sessionWoId != board.WorkOrderId)
                return Blocked(nameof(BlockReason.WrongWo), "Serial belongs to another work order.", qtyPerCarton);
            return await AddToCartonAsync(stationId, board, qtyPerCarton, repacked: true, ct);
        }

        var scan = await scans.ExecuteAsync(tx, new ScanCommand(stationId, serialNo, "OK", null, nameof(ScanSource.Kiosk)), ct);
        if (!scan.Recorded)
        {
            // Two packs of one board at once: the loser's scan reads the winner's as a duplicate; show its carton.
            if (scan.Response.Duplicate && board is not null && await LiveCartonAsync(board.Id, ct) is { } current)
                return AlreadyIn(current, qtyPerCarton);
            return Blocked(scan.Response.ReasonCode ?? "Duplicate", scan.Response.Message, qtyPerCarton);
        }
        return await AddToCartonAsync(stationId, board!, qtyPerCarton, repacked: false, ct);
    }

    // Step 4, after the commit (spec 2.2 trap 2): the carton exists for sure before its label does. A failure here does
    // not undo the pack: the carton is closed, the answer says so and the label is printed again from its endpoint.
    private async Task<PackResponse> WithLabelAsync(PackResponse response, CancellationToken ct)
    {
        try
        {
            var label = await labels.PrintCartonAsync(response.CartonNo!, reason: null, automatic: true, ct);
            return response with { LabelZpl = label.Zpl };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Carton {CartonNo} closed but its label was not made", response.CartonNo);
            return response with { Message = response.Message + " Label NOT printed: print it again." };
        }
    }

    // Spec 2.3: Qty goes up by a conditional UPDATE (never read-increment-write), and the CartonSerial row is only
    // inserted when it did. 0 rows = another pack filled or closed the carton meanwhile: read again, which opens a new one.
    private async Task<PackResponse> AddToCartonAsync(int stationId, Board board, int qtyPerCarton, bool repacked, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var by = user.EmployeeCode ?? throw new InvalidOperationException("Packing needs a logged-in user.");
        for (var attempt = 0; attempt < MaxCartonTries; attempt++)
        {
            var carton = await OpenCartonAsync(stationId, board, now, ct);
            if (carton.CheckAdd(board.WorkOrderId, board.ItemId, qtyPerCarton) is { } block)
            {
                // Full but still Open: the spec was lowered under what the carton holds. Close it, take a new one.
                if (block == PackBlock.CartonFull) { await CloseIfFullAsync(carton.Id, qtyPerCarton, now, ct); continue; }
                return Blocked(block.ToString(), $"Serial cannot go into carton {carton.CartonNo} ({block}).", qtyPerCarton);
            }

            var added = await db.Cartons
                .Where(c => c.Id == carton.Id && c.Status == CartonStatus.Open && c.Qty < qtyPerCarton)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.Qty, c => c.Qty + 1), ct) == 1;
            if (!added) continue;

            db.CartonSerials.Add(new CartonSerial(carton.Id, board.Id, by, now));   // P3: filtered unique index
            await db.SaveChangesAsync(ct);
            var closed = await CloseIfFullAsync(carton.Id, qtyPerCarton, now, ct);
            var qty = await db.Cartons.Where(c => c.Id == carton.Id).Select(c => c.Qty).SingleAsync(ct);

            var message = (closed ? $"Carton {carton.CartonNo} full ({qty}/{qtyPerCarton}): closed." : $"Packed into {carton.CartonNo} ({qty}/{qtyPerCarton}).")
                          + (repacked ? " Board had passed PACK already." : "");
            return new PackResponse("Ok", message, null, false, repacked, carton.CartonNo, qty, qtyPerCarton, closed);
        }
        throw new ConflictException("The carton is busy: scan the board again.");
    }

    // The station's Open carton of this WO, or a new one (P4). Two packs opening one at the same instant: the unique
    // index makes the second INSERT wait for the first transaction and fail once it commits, so it is there to read.
    private async Task<Carton> OpenCartonAsync(int stationId, Board board, DateTime now, CancellationToken ct)
    {
        var open = await OpenCartonQuery(stationId, board.WorkOrderId).SingleOrDefaultAsync(ct);
        if (open is not null) return open;

        var carton = new Carton(PackingNumberFormat.Carton(plant.DateOf(now), await sequences.NextCartonNumberAsync(ct)),
            board.WorkOrderId, board.ItemId, stationId, now);
        db.Cartons.Add(carton);
        try
        {
            await db.SaveChangesAsync(ct);
            return carton;
        }
        catch (ConflictException)
        {
            return await OpenCartonQuery(stationId, board.WorkOrderId).SingleAsync(ct);
        }
        finally
        {
            db.Entry(carton).State = EntityState.Detached;   // Qty and Status change by ExecuteUpdate from here on
        }
    }

    private IQueryable<Carton> OpenCartonQuery(int stationId, int woId)
        => db.Cartons.AsNoTracking().Where(c => c.StationId == stationId && c.WorkOrderId == woId && c.Status == CartonStatus.Open);

    // P5, conditional like the add: only the pack that made it full closes it.
    private async Task<bool> CloseIfFullAsync(int cartonId, int qtyPerCarton, DateTime now, CancellationToken ct)
        => await db.Cartons
            .Where(c => c.Id == cartonId && c.Status == CartonStatus.Open && c.Qty >= qtyPerCarton)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.Status, CartonStatus.Closed).SetProperty(c => c.ClosedAt, (DateTime?)now), ct) == 1;

    private sealed record CurrentCarton(string CartonNo, int Qty);

    // "WHERE SerialId = @id AND UnpackedAt IS NULL": the filtered unique index (spec 2.1 trap 1).
    private Task<CurrentCarton?> LiveCartonAsync(long serialId, CancellationToken ct)
        => (from cs in db.CartonSerials
            join c in db.Cartons on cs.CartonId equals c.Id
            where cs.SerialId == serialId && cs.UnpackedAt == null
            select new CurrentCarton(c.CartonNo, c.Qty)).SingleOrDefaultAsync(ct);

    private static PackResponse AlreadyIn(CurrentCarton carton, int qtyPerCarton)
        => new("Ok", $"Already in carton {carton.CartonNo} ({carton.Qty}/{qtyPerCarton}).", null, true, false,
            carton.CartonNo, carton.Qty, qtyPerCarton, false);

    private static PackResponse Blocked(string reasonCode, string message, int qtyPerCarton)
        => new("Blocked", message, reasonCode, false, false, null, 0, qtyPerCarton, false);
}
