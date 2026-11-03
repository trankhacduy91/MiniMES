using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MiniMes.Application.Common;
using MiniMes.Contracts.Packing;
using MiniMes.Domain.Common;
using MiniMes.Domain.Packing;
using MiniMes.Domain.Planning;

namespace MiniMes.Application.Packing;

// Spec M3b step 5: the supervisor's packing actions (P6, P8, P9 + partial pallet, user decision step 2). Each is one
// SaveChanges on tracked entities: a concurrent pack/palletize moves Qty/CartonCount by ExecuteUpdate, which changes
// the RowVersion, so the supervisor's stale view ends in 409 instead of overwriting the count.

internal static class PackingActionSupport
{
    public static async Task<Carton> TrackedCartonAsync(this IAppDbContext db, string cartonNo, CancellationToken ct)
    {
        var key = PackingReads.Key(cartonNo);
        return await db.Cartons.SingleOrDefaultAsync(c => c.CartonNo == key, ct) ?? throw new KeyNotFoundException($"Carton {key} not found.");
    }

    public static async Task<Pallet> TrackedPalletAsync(this IAppDbContext db, string palletNo, CancellationToken ct)
    {
        var key = PackingReads.Key(palletNo);
        return await db.Pallets.SingleOrDefaultAsync(p => p.PalletNo == key, ct) ?? throw new KeyNotFoundException($"Pallet {key} not found.");
    }

    public static async Task<PackingSpec> SpecAsync(this IAppDbContext db, int itemId, CancellationToken ct)
        => await db.PackingSpecs.AsNoTracking().SingleOrDefaultAsync(p => p.ItemId == itemId, ct)
           ?? throw new DomainException("No packing spec for the item.");

    public static string Supervisor(this ICurrentUser user)
        => user.EmployeeCode ?? throw new InvalidOperationException("A packing action needs a logged-in user.");
}


// ---------- POST /api/cartons/{no}/close-partial (P6) ----------

public sealed record ClosePartialCartonCommand(string CartonNo, string Reason) : IRequest<CartonDto>;

public sealed class ClosePartialCartonValidator : AbstractValidator<ClosePartialCartonCommand>
{
    public ClosePartialCartonValidator() => RuleFor(x => x.Reason).NotEmpty().MaximumLength(Carton.ReasonMaxLength);
}

public sealed class ClosePartialCartonHandler(IAppDbContext db, PackingLabels labels, ICurrentUser user, TimeProvider time,
    ILogger<ClosePartialCartonHandler> log) : IRequestHandler<ClosePartialCartonCommand, CartonDto>
{
    public async Task<CartonDto> Handle(ClosePartialCartonCommand cmd, CancellationToken ct)
    {
        var carton = await db.TrackedCartonAsync(cmd.CartonNo, ct);
        var spec = await db.SpecAsync(carton.ItemId, ct);
        carton.ClosePartial(cmd.Reason, user.Supervisor(), spec.QtyPerCarton, time.GetUtcNow().UtcDateTime);   // 422
        await db.SaveChangesAsync(ct);   // a pack in between -> 409, look again

        // After the save, like the automatic close (spec 2.2 trap 2); a failed label does not reopen the carton.
        string? zpl = null;
        try { zpl = (await labels.PrintCartonAsync(carton.CartonNo, reason: null, automatic: true, ct)).Zpl; }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Carton {CartonNo} closed short but its label was not made", carton.CartonNo);
        }
        return await db.CartonDtoAsync(carton.CartonNo, ct, zpl);
    }
}

// ---------- POST /api/cartons/{no}/unpack (P9) ----------

public sealed record UnpackSerialCommand(string CartonNo, string SerialNo, string Reason) : IRequest<CartonDto>;

public sealed class UnpackSerialValidator : AbstractValidator<UnpackSerialCommand>
{
    public UnpackSerialValidator()
    {
        RuleFor(x => x.SerialNo).NotEmpty().MaximumLength(Serial.SerialNoMaxLength);
        RuleFor(x => x.Reason).NotEmpty().MaximumLength(Carton.ReasonMaxLength);
    }
}

public sealed class UnpackSerialHandler(IAppDbContext db, ICurrentUser user, TimeProvider time) : IRequestHandler<UnpackSerialCommand, CartonDto>
{
    public async Task<CartonDto> Handle(UnpackSerialCommand cmd, CancellationToken ct)
    {
        var carton = await db.TrackedCartonAsync(cmd.CartonNo, ct);
        var serialNo = PackingReads.Key(cmd.SerialNo);
        var serialId = await db.Serials.Where(s => s.SerialNo == serialNo).Select(s => (long?)s.Id).SingleOrDefaultAsync(ct)
                       ?? throw new KeyNotFoundException($"Serial {serialNo} not found.");
        var row = await db.CartonSerials.SingleOrDefaultAsync(cs => cs.CartonId == carton.Id && cs.SerialId == serialId && cs.UnpackedAt == null, ct)
                  ?? throw new DomainException($"Serial {serialNo} is not in carton {carton.CartonNo}.");

        // A closed carton opens again to be filled up (user decision step 2): the station must not have another open
        // carton of this WO (P4). Said here in words; the unique index still catches the race (409).
        if (carton.Status == CartonStatus.Closed && carton.PalletId is null
            && await db.Cartons.AnyAsync(c => c.StationId == carton.StationId && c.WorkOrderId == carton.WorkOrderId
                                              && c.Status == CartonStatus.Open, ct))
            throw new DomainException($"The station has another open carton of this work order: close it before reopening {carton.CartonNo}.");

        carton.Unpack(row, user.Supervisor(), cmd.Reason, time.GetUtcNow().UtcDateTime);   // 422 on a pallet / shipped
        await db.SaveChangesAsync(ct);
        return await db.CartonDtoAsync(carton.CartonNo, ct);
    }
}

// ---------- POST /api/cartons/{no}/remove-from-pallet (P9 prerequisite) ----------

public sealed record RemoveCartonFromPalletCommand(string CartonNo) : IRequest<CartonDto>;

public sealed class RemoveCartonFromPalletHandler(IAppDbContext db) : IRequestHandler<RemoveCartonFromPalletCommand, CartonDto>
{
    public async Task<CartonDto> Handle(RemoveCartonFromPalletCommand cmd, CancellationToken ct)
    {
        var carton = await db.TrackedCartonAsync(cmd.CartonNo, ct);
        if (carton.PalletId is not { } palletId) throw new DomainException($"Carton {carton.CartonNo} is not on a pallet.");
        var pallet = await db.Pallets.SingleAsync(p => p.Id == palletId, ct);

        // A closed pallet opens again; one open pallet per (station, item), like the carton.
        if (pallet.Status == PalletStatus.Closed
            && await db.Pallets.AnyAsync(p => p.StationId == pallet.StationId && p.ItemId == pallet.ItemId && p.Status == PalletStatus.Open, ct))
            throw new DomainException($"The station has another open pallet of this item: close it before reopening {pallet.PalletNo}.");

        pallet.RemoveCarton(carton);   // 422 when shipped
        await db.SaveChangesAsync(ct);
        return await db.CartonDtoAsync(carton.CartonNo, ct);
    }
}

// ---------- POST /api/pallets/{no}/close-partial (user decision step 2) ----------

public sealed record ClosePartialPalletCommand(string PalletNo, string Reason) : IRequest<PalletDto>;

public sealed class ClosePartialPalletValidator : AbstractValidator<ClosePartialPalletCommand>
{
    public ClosePartialPalletValidator() => RuleFor(x => x.Reason).NotEmpty().MaximumLength(Carton.ReasonMaxLength);
}

public sealed class ClosePartialPalletHandler(IAppDbContext db, PackingLabels labels, ICurrentUser user, TimeProvider time,
    ILogger<ClosePartialPalletHandler> log) : IRequestHandler<ClosePartialPalletCommand, PalletDto>
{
    public async Task<PalletDto> Handle(ClosePartialPalletCommand cmd, CancellationToken ct)
    {
        var pallet = await db.TrackedPalletAsync(cmd.PalletNo, ct);
        var spec = await db.SpecAsync(pallet.ItemId, ct);
        pallet.ClosePartial(cmd.Reason, user.Supervisor(), spec.CartonsPerPallet, time.GetUtcNow().UtcDateTime);
        await db.SaveChangesAsync(ct);

        string? zpl = null;
        try { zpl = (await labels.PrintPalletAsync(pallet.PalletNo, reason: null, automatic: true, ct)).Zpl; }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Pallet {PalletNo} closed short but its label was not made", pallet.PalletNo);
        }
        return await db.PalletDtoAsync(pallet.PalletNo, ct, zpl);
    }
}

// ---------- POST /api/pallets/{no}/ship (P8) ----------

public sealed record ShipPalletCommand(string PalletNo, string ShipmentRef) : IRequest<PalletDto>;

public sealed class ShipPalletValidator : AbstractValidator<ShipPalletCommand>
{
    public ShipPalletValidator() => RuleFor(x => x.ShipmentRef).NotEmpty().MaximumLength(Pallet.ShipmentRefMaxLength);
}

// The pallet, its cartons and the Hold check in one SaveChanges. Spec 2.4 locks the pallet row (UPDLOCK); here the
// RowVersion of the pallet and of each carton does the same job: a carton added, removed or unpacked meanwhile makes
// the save fail with 409 instead of shipping a pallet that changed. (A serial put on Hold in the same instant is not
// covered: M7's Hold is the source, and it can block a shipped pallet by NCR.)
public sealed class ShipPalletHandler(IAppDbContext db, ICurrentUser user, TimeProvider time, ILogger<ShipPalletHandler> log)
    : IRequestHandler<ShipPalletCommand, PalletDto>
{
    public async Task<PalletDto> Handle(ShipPalletCommand cmd, CancellationToken ct)
    {
        var pallet = await db.TrackedPalletAsync(cmd.PalletNo, ct);
        var cartons = await db.Cartons.Where(c => c.PalletId == pallet.Id).ToListAsync(ct);
        var held = await db.HeldCartonIdsAsync(cartons.Select(c => c.Id).ToList(), ct);
        var by = user.Supervisor();

        pallet.Ship(cmd.ShipmentRef, held.Count > 0, cartons, by, time.GetUtcNow().UtcDateTime);   // 422 open / held / no ref
        await db.SaveChangesAsync(ct);
        log.LogInformation("Pallet {PalletNo} shipped by {EmployeeCode} with {ShipmentRef}: {CartonCount} cartons",
            pallet.PalletNo, by, pallet.ShipmentRef, cartons.Count);
        return await db.PalletDtoAsync(pallet.PalletNo, ct);
    }
}
