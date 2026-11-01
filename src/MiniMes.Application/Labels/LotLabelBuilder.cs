using System.Globalization;

namespace MiniMes.Application.Labels;

// Spec M6 step 2: the reel label, same 60 x 30 mm stock as the product label (ZplLabelBuilder). Code128 of LotNo is
// what the kiosk scans; item code, supplier lot, quantity and expiry are for the person picking the reel.
// Expiry as "03 SEP 2028": 03/09 vs 09/03 is the classic misread (spec 1.5). [v2] A lot waiting for IQC says so in
// a black band: the yellow "quarantine" sticker of the warehouse, printed.
public static class LotLabelBuilder
{
    public static string Build(string lotNo, string itemCode, string? supplierLot, decimal qty, string uom, DateOnly? expiry,
        bool pendingIqc)
    {
        foreach (var value in new[] { lotNo, itemCode, uom }) ZplLabelBuilder.RequireSafe(value);
        if (supplierLot is not null) ZplLabelBuilder.RequireSafe(supplierLot);

        var qtyText = qty.ToString("0.###", CultureInfo.InvariantCulture);
        var expiryText = expiry is { } e ? e.ToString("dd MMM yyyy", CultureInfo.InvariantCulture).ToUpperInvariant() : "NONE";
        var iqc = pendingIqc
            ? $"^FO300,180^GB170,40,40^FS^FO310,190^A0N,24,24^FR^FDPENDING IQC^FS{Environment.NewLine}"
            : "";
        return $"""
            ^XA
            ^PW{ZplLabelBuilder.WidthDots}
            ^LL{ZplLabelBuilder.HeightDots}
            ^FO20,15^A0N,30,30^FD{itemCode}^FS
            ^FO20,55^BY2^BCN,60,Y,N,N,A^FD{lotNo}^FS
            ^FO20,150^A0N,22,22^FDSUP {supplierLot ?? "-"}   QTY {qtyText} {uom}^FS
            ^FO20,185^A0N,22,22^FDEXP {expiryText}^FS
            {iqc}^XZ
            """;
    }
}
