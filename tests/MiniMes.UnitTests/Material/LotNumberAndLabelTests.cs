using MiniMes.Application.Labels;
using MiniMes.Domain.Common;
using MiniMes.Domain.Material;

namespace MiniMes.UnitTests.Material;

// Spec M6 step 2: the lot number (user decision: L-yyMMdd-seq6) and the reel label, as strings.
public class LotNumberAndLabelTests
{
    [Theory]
    [InlineData(1, "L-261101-000001")]
    [InlineData(17, "L-261101-000017")]
    [InlineData(999_999, "L-261101-999999")]
    public void Lot_number_is_date_and_sequence(long sequence, string expected)
    {
        var lotNo = LotNumberFormat.Build(new DateOnly(2026, 11, 1), sequence);

        Assert.Equal(expected, lotNo);
        Assert.True(lotNo.Length <= MaterialLot.LotNoMaxLength);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1_000_000)]
    public void Lot_sequence_out_of_range_is_refused(long sequence)
        => Assert.Throws<DomainException>(() => LotNumberFormat.Build(new DateOnly(2026, 11, 1), sequence));

    [Fact]
    public void Label_has_the_scannable_lot_and_what_a_person_needs()
    {
        var zpl = LotLabelBuilder.Build("L-260915-000003", "CAP-100N-0402", "MUR-2609-A17", 10_000m, "pcs",
            new DateOnly(2028, 9, 3), pendingIqc: false);

        Assert.StartsWith("^XA", zpl);
        Assert.EndsWith("^XZ", zpl.TrimEnd());
        Assert.Contains("^BCN,60,Y,N,N,A^FDL-260915-000003^FS", zpl);   // Code128 of LotNo: what the kiosk scans
        Assert.Contains("^FDCAP-100N-0402^FS", zpl);
        Assert.Contains("SUP MUR-2609-A17   QTY 10000 pcs", zpl);
        Assert.Contains("EXP 03 SEP 2028", zpl);                          // never 03/09 vs 09/03 (spec 1.5)
        Assert.DoesNotContain("PENDING IQC", zpl);
    }

    [Fact]
    public void Label_of_a_lot_waiting_for_iqc_says_so_and_handles_no_expiry_and_kilograms()
    {
        var zpl = LotLabelBuilder.Build("L-261101-000001", "SOLDER-PASTE", null, 0.5m, "kg", null, pendingIqc: true);

        Assert.Contains("PENDING IQC", zpl);
        Assert.Contains("SUP -   QTY 0.5 kg", zpl);
        Assert.Contains("EXP NONE", zpl);
    }

    [Fact]
    public void Label_refuses_data_that_would_run_as_zpl()
        => Assert.Throws<DomainException>(() => LotLabelBuilder.Build("L-261101-000001", "CAP", "A^XZ", 1, "pcs", null, false));
}
