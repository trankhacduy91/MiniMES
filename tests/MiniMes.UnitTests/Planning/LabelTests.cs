using MiniMes.Application.Labels;
using MiniMes.Domain.Common;
using MiniMes.Domain.Planning;

namespace MiniMes.UnitTests.Planning;

// Spec M2 step 6: the ZPL string holds the serial as Code128 and DataMatrix, and refuses ZPL control characters.
public class LabelTests
{
    private const string SerialNo = "PCBA2610010001232";

    [Fact]
    public void Label_has_both_barcodes_with_the_serial_and_the_header()
    {
        var zpl = ZplLabelBuilder.Build("PCB-A", "WO-2610-00001", SerialNo, bomVersion: 3, routingVersion: 2);

        Assert.StartsWith("^XA", zpl);
        Assert.EndsWith("^XZ", zpl);
        Assert.Contains("^PW480", zpl);                                     // 60 mm at 8 dots/mm
        Assert.Contains("^LL240", zpl);                                     // 30 mm
        Assert.Matches(@"\^BC[^\^]*\^FD" + SerialNo + @"\^FS", zpl);        // Code128 field
        Assert.Matches(@"\^BX[^\^]*\^FD" + SerialNo + @"\^FS", zpl);        // DataMatrix field
        Assert.Contains("^FDPCB-A   WO-2610-00001^FS", zpl);
        Assert.Contains("^FDBOM v3   RT v2^FS", zpl);
    }

    [Theory]
    [InlineData("PCB^A")]     // '^' starts a ZPL command
    [InlineData("PCB~A")]     // '~' starts a control command
    [InlineData("Bo mạch")]   // the built-in font has no Vietnamese glyphs
    [InlineData("PCB\nA")]
    public void Unprintable_or_command_characters_are_refused(string itemCode)
        => Assert.Throws<DomainException>(() => ZplLabelBuilder.Build(itemCode, "WO-2610-00001", SerialNo, 1, 1));

    // ---------- LabelPrintLog (rule R13) ----------

    private static readonly DateTime Now = new(2026, 10, 1, 1, 0, 0, DateTimeKind.Utc);
    private static Serial NewSerial() => new(SerialNo, workOrderId: 1, itemId: 1);

    [Fact]
    public void First_print_needs_no_reason()
    {
        var log = new LabelPrintLog(NewSerial(), printNo: 1, "planner01", reason: null, Now);

        Assert.Equal((1, (string?)null, SerialNo), (log.PrintNo, log.Reason, log.SerialNo));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    public void Reprint_without_a_reason_is_refused(string? reason)
        => Assert.Throws<DomainException>(() => new LabelPrintLog(NewSerial(), printNo: 2, "sup01", reason, Now));

    [Fact]
    public void Print_number_starts_at_1()
        => Assert.Throws<DomainException>(() => new LabelPrintLog(NewSerial(), printNo: 0, "planner01", "x", Now));
}
