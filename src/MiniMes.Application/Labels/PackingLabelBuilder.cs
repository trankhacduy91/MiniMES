using System.Globalization;

namespace MiniMes.Application.Labels;

// Spec M3b step 4: carton and pallet labels, 4 x 3 inch stock at 203 dpi = 812 x 609 dots (user decision), read from
// a few metres in the warehouse. Code128 of the carton/pallet number is what the Palletize kiosk and the customer's
// goods receipt scan; item, quantity, WO and date are for people. A carton closed short by a supervisor says PARTIAL
// in a black band (P6), so nobody counts it as a full one.
public static class PackingLabelBuilder
{
    public const int WidthDots = 812;
    public const int HeightDots = 609;
    public const string LabelaryInches = "4x3";

    public static string Carton(string cartonNo, string itemCode, int qty, string woNo, DateOnly plantDate, bool isPartial)
    {
        foreach (var value in new[] { cartonNo, itemCode, woNo }) ZplLabelBuilder.RequireSafe(value);
        return Build("CARTON", cartonNo, isPartial,
            $"^FO30,300^A0N,56,56^FDITEM {itemCode}^FS",
            $"^FO30,375^A0N,56,56^FDQTY {qty} PCS^FS",
            $"^FO30,460^A0N,36,36^FDWO {woNo}^FS",
            $"^FO30,515^A0N,36,36^FDDATE {Date(plantDate)}^FS");
    }

    public static string Pallet(string palletNo, string itemCode, int cartonCount, int totalQty, DateOnly plantDate, bool isPartial)
    {
        foreach (var value in new[] { palletNo, itemCode }) ZplLabelBuilder.RequireSafe(value);
        return Build("PALLET", palletNo, isPartial,
            $"^FO30,300^A0N,56,56^FDITEM {itemCode}^FS",
            $"^FO30,375^A0N,56,56^FDCARTONS {cartonCount}   QTY {totalQty} PCS^FS",
            $"^FO30,460^A0N,36,36^FDDATE {Date(plantDate)}^FS");
    }

    // "03 NOV 2026", like the reel label: 03/11 vs 11/03 is the classic misread (M6 spec 1.5).
    private static string Date(DateOnly d) => d.ToString("dd MMM yyyy", CultureInfo.InvariantCulture).ToUpperInvariant();

    private static string Build(string title, string number, bool isPartial, params string[] lines)
    {
        // ^BCN,130,Y,N,N,A: Code128 130 dots high with the number printed below; mode A packs the 12 digits as pairs.
        var partial = isPartial ? $"^FO560,20^GB230,60,60^FS^FO585,32^A0N,40,40^FR^FDPARTIAL^FS{Environment.NewLine}" : "";
        return $"""
            ^XA
            ^PW{WidthDots}
            ^LL{HeightDots}
            ^FO30,30^A0N,44,44^FD{title}^FS
            {partial}^FO30,100^BY3^BCN,130,Y,N,N,A^FD{number}^FS
            {string.Join(Environment.NewLine, lines)}
            ^XZ
            """;
    }
}
