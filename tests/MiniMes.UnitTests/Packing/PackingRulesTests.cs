using MiniMes.Domain.Common;
using MiniMes.Domain.Packing;

namespace MiniMes.UnitTests.Packing;

// Spec M3b step 2: P2, P5-P9 as pure rules. PCB-A packs 10 per carton, 20 cartons per pallet; WO 7 makes item 3 at
// station 5. Quantities only move through conditional UPDATEs in the database (2.3), so the tests set the state an
// entity would have when read back (State helper).
public class PackingRulesTests
{
    private static readonly DateTime Now = new(2026, 11, 3, 6, 0, 0, DateTimeKind.Utc);
    private const int Wo = 7, Item = 3, Station = 5, PerCarton = 10, PerPallet = 20;

    private static Carton NewCarton(int qty = 0, CartonStatus status = CartonStatus.Open, int? palletId = null, int id = 100)
        => new Carton("C261103000001", Wo, Item, Station, Now)
            .With(nameof(Carton.Id), id).With(nameof(Carton.Qty), qty).With(nameof(Carton.Status), status).With(nameof(Carton.PalletId), palletId);

    private static Pallet NewPallet(int cartons = 0, PalletStatus status = PalletStatus.Open, int id = 900)
        => new Pallet("P261103000001", Item, Station, Now)
            .With(nameof(Pallet.Id), id).With(nameof(Pallet.CartonCount), cartons).With(nameof(Pallet.Status), status);

    // ---------- P2 + P5: what may go into a carton ----------

    public static TheoryData<string, Carton, int, int, PackBlock?> AddCases() => new()
    {
        { "same WO and item, room left", NewCarton(qty: 9), Wo, Item, null },
        { "empty carton", NewCarton(), Wo, Item, null },
        { "another model (PCB-A2 in a PCB-A carton)", NewCarton(qty: 3), Wo, 4, PackBlock.WrongItem },
        { "another WO of the same model", NewCarton(qty: 3), 8, Item, PackBlock.WrongWorkOrder },
        { "carton full (P5: never 11)", NewCarton(qty: PerCarton), Wo, Item, PackBlock.CartonFull },
        { "carton closed", NewCarton(qty: 4, status: CartonStatus.Closed), Wo, Item, PackBlock.CartonNotOpen },
        { "carton shipped", NewCarton(qty: 10, status: CartonStatus.Shipped), Wo, Item, PackBlock.CartonNotOpen },
        // State before content: a closed carton says "closed", whatever is scanned.
        { "closed and another model", NewCarton(qty: 4, status: CartonStatus.Closed), Wo, 4, PackBlock.CartonNotOpen },
        // The model before the WO: a different model in the box is the expensive mistake.
        { "another model and another WO", NewCarton(qty: 3), 8, 4, PackBlock.WrongItem },
    };

    [Theory]
    [MemberData(nameof(AddCases))]
    public void Carton_add_table(string scenario, Carton carton, int serialWo, int serialItem, PackBlock? expected)
        => Assert.True(carton.CheckAdd(serialWo, serialItem, PerCarton) == expected, $"{scenario}: got {carton.CheckAdd(serialWo, serialItem, PerCarton)}");

    // ---------- P6: partial carton ----------

    [Fact]
    public void Supervisor_closes_a_short_carton_with_a_reason()
    {
        var carton = NewCarton(qty: 7);

        carton.ClosePartial("  WO end, 7 pcs ", "sup01", PerCarton, Now);

        Assert.Equal((CartonStatus.Closed, true, "sup01", "WO end, 7 pcs", (DateTime?)Now),
            (carton.Status, carton.IsPartial, carton.ClosedBy, carton.CloseReason, carton.ClosedAt));
    }

    [Theory]
    [InlineData(7, CartonStatus.Open, "", "reason")]                      // no reason
    [InlineData(0, CartonStatus.Open, "WO end", "empty")]                 // empty: just not used
    [InlineData(10, CartonStatus.Open, "WO end", "full")]                 // full closes by itself
    [InlineData(7, CartonStatus.Closed, "WO end", "not open")]
    public void Partial_close_is_refused(int qty, CartonStatus status, string reason, string expectedInMessage)
    {
        var carton = NewCarton(qty: qty, status: status);

        var ex = Assert.Throws<DomainException>(() => carton.ClosePartial(reason, "sup01", PerCarton, Now));

        Assert.Contains(expectedInMessage, ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(status, carton.Status);
    }

    [Fact]
    public void Partial_close_refuses_a_reason_longer_than_the_column()
        => Assert.Throws<DomainException>(() => NewCarton(qty: 7).ClosePartial(new string('x', Carton.ReasonMaxLength + 1), "sup01", PerCarton, Now));

    // ---------- P9: unpack ----------


    [Fact]
    public void Unpack_from_a_closed_partial_carton_reopens_it_for_refill()
    {
        var carton = NewCarton(qty: 7);
        carton.ClosePartial("WO end, 7 pcs", "sup01", PerCarton, Now);
        var row = new CartonSerial(carton.Id, 42, "EMP00123", Now.AddMinutes(-30));

        carton.Unpack(row, "sup01", "NCR-0007 confirmed defect", Now);

        Assert.Equal((Now, "sup01", "NCR-0007 confirmed defect"), (row.UnpackedAt!.Value, row.UnpackedBy, row.UnpackReason));
        Assert.Equal((6, CartonStatus.Open, false, (string?)null, (string?)null, (DateTime?)null),
            (carton.Qty, carton.Status, carton.IsPartial, carton.ClosedBy, carton.CloseReason, carton.ClosedAt));
    }

    [Fact]
    public void Unpack_from_an_open_carton_only_lowers_the_count()
    {
        var carton = NewCarton(qty: 4);

        carton.Unpack(new CartonSerial(carton.Id, 42, "EMP00123", Now), "sup01", "Wrong board", Now);

        Assert.Equal((3, CartonStatus.Open), (carton.Qty, carton.Status));
    }

    public static TheoryData<string, Carton, Func<Carton, CartonSerial>, string, string> UnpackRefusals() => new()
    {
        { "carton on a pallet: take it off first", NewCarton(qty: 10, status: CartonStatus.Closed, palletId: 900), c => Row(c), "NCR", "pallet" },
        { "carton shipped (P8)", NewCarton(qty: 10, status: CartonStatus.Shipped, palletId: 900), c => Row(c), "NCR", "shipped" },
        { "no reason", NewCarton(qty: 4), c => Row(c), " ", "reason" },
        { "serial of another carton", NewCarton(qty: 4), c => new CartonSerial(c.Id + 1, 42, "EMP00123", Now), "NCR", "not in carton" },
        { "serial already unpacked", NewCarton(qty: 4), c => Unpacked(Row(c)), "NCR", "not in carton" },
    };

    [Theory]
    [MemberData(nameof(UnpackRefusals))]
    public void Unpack_is_refused(string scenario, Carton carton, Func<Carton, CartonSerial> row, string reason, string expectedInMessage)
    {
        var target = row(carton);
        var before = (carton.Qty, carton.Status, target.UnpackedAt);

        var ex = Assert.Throws<DomainException>(() => carton.Unpack(target, "sup01", reason, Now));

        Assert.True(ex.Message.Contains(expectedInMessage, StringComparison.OrdinalIgnoreCase), $"{scenario}: {ex.Message}");
        Assert.Equal(before, (carton.Qty, carton.Status, target.UnpackedAt));
    }

    private static CartonSerial Row(Carton c) => new(c.Id, 42, "EMP00123", Now.AddHours(-1));

    private static CartonSerial Unpacked(CartonSerial row) => row.With(nameof(CartonSerial.UnpackedAt), (DateTime?)Now.AddMinutes(-5));

    // ---------- P7: what may go onto a pallet ----------

    public static TheoryData<string, Pallet, Carton, bool, PalletizeBlock?> PalletizeCases() => new()
    {
        { "closed carton, same item, room left", NewPallet(cartons: 19), NewCarton(qty: 10, status: CartonStatus.Closed), false, null },
        { "partial carton is fine", NewPallet(), NewCarton(qty: 7, status: CartonStatus.Closed).With(nameof(Carton.IsPartial), true), false, null },
        { "carton still open", NewPallet(), NewCarton(qty: 6), false, PalletizeBlock.CartonNotClosed },
        { "carton of another item", NewPallet(), NewCarton(qty: 10, status: CartonStatus.Closed).With(nameof(Carton.ItemId), 4), false, PalletizeBlock.WrongItem },
        { "carton already on a pallet", NewPallet(), NewCarton(qty: 10, status: CartonStatus.Closed, palletId: 901), false, PalletizeBlock.CartonOnAPallet },
        { "carton with a held serial (P10)", NewPallet(), NewCarton(qty: 10, status: CartonStatus.Closed), true, PalletizeBlock.CartonOnHold },
        { "pallet full", NewPallet(cartons: PerPallet), NewCarton(qty: 10, status: CartonStatus.Closed), false, PalletizeBlock.PalletFull },
        { "pallet closed", NewPallet(cartons: 20, status: PalletStatus.Closed), NewCarton(qty: 10, status: CartonStatus.Closed), false, PalletizeBlock.PalletNotOpen },
        { "pallet shipped", NewPallet(cartons: 20, status: PalletStatus.Shipped), NewCarton(qty: 10, status: CartonStatus.Closed), false, PalletizeBlock.PalletNotOpen },
        // What the carton is before its state: another model says "wrong item" even when also open.
        { "open carton of another item", NewPallet(), NewCarton(qty: 3).With(nameof(Carton.ItemId), 4), false, PalletizeBlock.WrongItem },
    };

    [Theory]
    [MemberData(nameof(PalletizeCases))]
    public void Palletize_table(string scenario, Pallet pallet, Carton carton, bool onHold, PalletizeBlock? expected)
        => Assert.True(pallet.CheckAdd(carton, onHold, PerPallet) == expected, $"{scenario}: got {pallet.CheckAdd(carton, onHold, PerPallet)}");

    [Fact]
    public void Carton_goes_on_a_pallet_once()
    {
        var carton = NewCarton(qty: 10, status: CartonStatus.Closed);

        carton.PutOnPallet(900);

        Assert.Equal(900, carton.PalletId);
        Assert.Throws<DomainException>(() => carton.PutOnPallet(901));
        Assert.Throws<DomainException>(() => NewCarton(qty: 4).PutOnPallet(900));   // open carton
    }

    // ---------- partial pallet (user decision), remove from pallet ----------

    [Fact]
    public void Supervisor_closes_a_short_pallet()
    {
        var pallet = NewPallet(cartons: 7);

        pallet.ClosePartial("WO end", "sup01", PerPallet, Now);

        Assert.Equal((PalletStatus.Closed, true, "sup01", "WO end"), (pallet.Status, pallet.IsPartial, pallet.ClosedBy, pallet.CloseReason));
        Assert.Throws<DomainException>(() => NewPallet(cartons: 0).ClosePartial("WO end", "sup01", PerPallet, Now));
        Assert.Throws<DomainException>(() => NewPallet(cartons: PerPallet).ClosePartial("WO end", "sup01", PerPallet, Now));
        Assert.Throws<DomainException>(() => NewPallet(cartons: 7).ClosePartial("", "sup01", PerPallet, Now));
    }

    [Fact]
    public void Removing_a_carton_reopens_a_closed_pallet()
    {
        var pallet = NewPallet(cartons: 7);
        pallet.ClosePartial("WO end", "sup01", PerPallet, Now);
        var carton = NewCarton(qty: 10, status: CartonStatus.Closed, palletId: pallet.Id);

        pallet.RemoveCarton(carton);

        Assert.Null(carton.PalletId);
        Assert.Equal((6, PalletStatus.Open, false, (string?)null), (pallet.CartonCount, pallet.Status, pallet.IsPartial, pallet.CloseReason));
    }

    [Fact]
    public void Removing_is_refused_from_a_shipped_pallet_or_for_a_carton_elsewhere()
    {
        var shipped = NewPallet(cartons: 20, status: PalletStatus.Shipped);
        Assert.Throws<DomainException>(() => shipped.RemoveCarton(NewCarton(qty: 10, status: CartonStatus.Shipped, palletId: shipped.Id)));
        Assert.Throws<DomainException>(() => NewPallet(cartons: 3).RemoveCarton(NewCarton(qty: 10, status: CartonStatus.Closed, palletId: 901)));
    }

    // ---------- P8: ship ----------

    private static (Pallet Pallet, List<Carton> Cartons) ClosedPallet(int cartons = 2)
    {
        var pallet = NewPallet(cartons: cartons, status: PalletStatus.Closed);
        return (pallet, Enumerable.Range(0, cartons).Select(i => NewCarton(qty: 10, status: CartonStatus.Closed, palletId: pallet.Id, id: 100 + i)).ToList());
    }

    [Fact]
    public void Ship_freezes_the_pallet_and_its_cartons()
    {
        var (pallet, cartons) = ClosedPallet();

        pallet.Ship(" DO-2610-015 ", hasHeldSerial: false, cartons, "sup01", Now);

        Assert.Equal((PalletStatus.Shipped, "DO-2610-015", "sup01", (DateTime?)Now), (pallet.Status, pallet.ShipmentRef, pallet.ShippedBy, pallet.ShippedAt));
        Assert.All(cartons, c => Assert.Equal(CartonStatus.Shipped, c.Status));
    }

    public static TheoryData<string, PalletStatus, bool, string, string> ShipRefusals() => new()
    {
        { "held serial inside (P8, P10)", PalletStatus.Closed, true, "DO-1", "held serials" },
        { "pallet still open", PalletStatus.Open, false, "DO-1", "not closed" },
        { "already shipped", PalletStatus.Shipped, false, "DO-1", "already shipped" },
        { "no delivery order", PalletStatus.Closed, false, " ", "shipment reference" },
        { "delivery order too long", PalletStatus.Closed, false, new string('D', Pallet.ShipmentRefMaxLength + 1), "at most" },
    };

    [Theory]
    [MemberData(nameof(ShipRefusals))]
    public void Ship_is_refused(string scenario, PalletStatus status, bool held, string shipmentRef, string expectedInMessage)
    {
        var (pallet, cartons) = ClosedPallet();
        pallet.With(nameof(Pallet.Status), status);

        var ex = Assert.Throws<DomainException>(() => pallet.Ship(shipmentRef, held, cartons, "sup01", Now));

        Assert.True(ex.Message.Contains(expectedInMessage, StringComparison.OrdinalIgnoreCase), $"{scenario}: {ex.Message}");
        Assert.Equal(status, pallet.Status);
        Assert.All(cartons, c => Assert.Equal(CartonStatus.Closed, c.Status));
    }

    [Fact]
    public void Ship_refuses_a_carton_list_that_does_not_match_the_pallet()
    {
        var (pallet, cartons) = ClosedPallet(3);

        Assert.Throws<DomainException>(() => pallet.Ship("DO-1", false, cartons.Take(2), "sup01", Now));
        Assert.Throws<DomainException>(() => pallet.Ship("DO-1", false, [.. cartons.Take(2), NewCarton(qty: 10, status: CartonStatus.Closed, palletId: 901)], "sup01", Now));
        Assert.Equal(PalletStatus.Closed, pallet.Status);
        Assert.All(cartons, c => Assert.Equal(CartonStatus.Closed, c.Status));
    }

    // ---------- numbers ----------

    [Fact]
    public void Carton_and_pallet_numbers()
    {
        var day = new DateOnly(2026, 11, 3);

        Assert.Equal("C261103000123", PackingNumberFormat.Carton(day, 123));
        Assert.Equal("P261103000004", PackingNumberFormat.Pallet(day, 4));
        Assert.Throws<DomainException>(() => PackingNumberFormat.Carton(day, 0));
        Assert.Throws<DomainException>(() => PackingNumberFormat.Carton(day, PackingNumberFormat.MaxSequence + 1));
    }

    // Spec 2.5: at the Pack kiosk the shape tells a carton label from a pallet label from a serial (PCBA...).
    [Theory]
    [InlineData("C261103000123", true, false)]
    [InlineData("P261103000004", false, true)]
    [InlineData("PCBA2610170000011", false, false)]
    [InlineData("C26110300012", false, false)]       // one digit short
    [InlineData("c261103000123", false, false)]      // the server upper-cases first
    [InlineData("L-261101-000004", false, false)]    // a lot label
    public void Scanned_codes_are_told_apart(string code, bool carton, bool pallet)
        => Assert.Equal((carton, pallet), (PackingNumberFormat.IsCartonNo(code), PackingNumberFormat.IsPalletNo(code)));
}

// Sets a private setter the way EF does when it reads a row: the state of a carton after the database counted it.
internal static class State
{
    public static T With<T>(this T entity, string property, object? value) where T : class
    {
        typeof(T).GetProperty(property)!.SetValue(entity, value);
        return entity;
    }
}
