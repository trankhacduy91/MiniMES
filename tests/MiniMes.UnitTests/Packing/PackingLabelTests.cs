using MiniMes.Application.Labels;
using MiniMes.Domain.Common;
using MiniMes.Domain.Packing;
using MiniMes.Domain.Planning;

namespace MiniMes.UnitTests.Packing;

// Spec M3b step 4: carton / pallet label content as a ZPL string (4 x 3 inch), and their rows in LabelPrintLog.
public class PackingLabelTests
{
    private static readonly DateOnly Day = new(2026, 11, 3);
    private static readonly DateTime Now = new(2026, 11, 3, 6, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Carton_label_has_the_number_as_code128_and_what_the_warehouse_reads()
    {
        var zpl = PackingLabelBuilder.Carton("C261103000123", "PCB-A", 10, "WO-2610-00001", Day, isPartial: false);

        Assert.StartsWith("^XA", zpl);
        Assert.EndsWith("^XZ", zpl);
        Assert.Contains("^PW812", zpl);                                       // 4 inch at 8 dots/mm
        Assert.Contains("^LL609", zpl);                                       // 3 inch
        Assert.Matches(@"\^BC[^\^]*\^FDC261103000123\^FS", zpl);              // Code128 field
        Assert.Contains("^FDITEM PCB-A^FS", zpl);
        Assert.Contains("^FDQTY 10 PCS^FS", zpl);
        Assert.Contains("^FDWO WO-2610-00001^FS", zpl);
        Assert.Contains("^FDDATE 03 NOV 2026^FS", zpl);
        Assert.DoesNotContain("PARTIAL", zpl);
    }

    [Fact]
    public void Partial_carton_says_so()
        => Assert.Contains("^FDPARTIAL^FS", PackingLabelBuilder.Carton("C261103000124", "PCB-A", 7, "WO-2610-00001", Day, isPartial: true));

    [Fact]
    public void Pallet_label_has_cartons_and_total_quantity()
    {
        var zpl = PackingLabelBuilder.Pallet("P261103000004", "PCB-A", 20, 200, Day, isPartial: false);

        Assert.Matches(@"\^BC[^\^]*\^FDP261103000004\^FS", zpl);
        Assert.Contains("^FDCARTONS 20   QTY 200 PCS^FS", zpl);
        Assert.Contains("^FDPALLET^FS", zpl);
    }

    [Theory]
    [InlineData("PCB^A")]
    [InlineData("PCB~A")]
    public void Command_characters_in_the_item_are_refused(string itemCode)
        => Assert.Throws<DomainException>(() => PackingLabelBuilder.Carton("C261103000123", itemCode, 10, "WO-2610-00001", Day, false));

    // ---------- LabelPrintLog for cartons and pallets ----------

    private static Carton NewCarton(CartonStatus status)
        => new Carton("C261103000123", 7, 3, 5, Now).With(nameof(Carton.Qty), 10).With(nameof(Carton.Status), status);

    private static Pallet NewPallet(PalletStatus status)
        => new Pallet("P261103000004", 3, 5, Now).With(nameof(Pallet.Status), status);

    [Fact]
    public void Carton_print_is_logged_by_its_number_without_a_serial()
    {
        var log = LabelPrintLog.ForCarton(NewCarton(CartonStatus.Closed), 1, "EMP00123", null, Now);

        Assert.Equal((LabelType.Carton, "C261103000123", (int?)7, (long?)null, (string?)null, 1),
            (log.LabelType, log.TargetKey, log.WorkOrderId, log.SerialId, log.SerialNo, log.PrintNo));
    }

    [Fact]
    public void Pallet_print_belongs_to_no_work_order()
    {
        var log = LabelPrintLog.ForPallet(NewPallet(PalletStatus.Closed), 1, "EMP00123", null, Now);

        Assert.Equal((LabelType.Pallet, "P261103000004", (int?)null), (log.LabelType, log.TargetKey, log.WorkOrderId));
    }

    [Fact]
    public void Open_carton_or_pallet_has_no_label_yet()
    {
        Assert.Throws<DomainException>(() => LabelPrintLog.ForCarton(NewCarton(CartonStatus.Open), 1, "EMP00123", null, Now));
        Assert.Throws<DomainException>(() => LabelPrintLog.ForPallet(NewPallet(PalletStatus.Open), 1, "EMP00123", null, Now));
    }

    [Fact]
    public void Shipped_carton_label_can_be_reprinted_with_a_reason()
    {
        var log = LabelPrintLog.ForCarton(NewCarton(CartonStatus.Shipped), 2, "sup01", "Label torn at customer", Now);

        Assert.Equal((2, "Label torn at customer"), (log.PrintNo, log.Reason));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(" ")]
    public void Carton_reprint_without_a_reason_is_refused(string? reason)
        => Assert.Throws<DomainException>(() => LabelPrintLog.ForCarton(NewCarton(CartonStatus.Closed), 2, "sup01", reason, Now));

    [Fact]
    public void Serial_label_keeps_its_serial_and_key()
    {
        var log = new LabelPrintLog(new Serial("PCBA2610010001232", 1, 1), 1, "planner01", null, Now);

        Assert.Equal((LabelType.Serial, "PCBA2610010001232", "PCBA2610010001232"), (log.LabelType, log.TargetKey, log.SerialNo));
    }
}
