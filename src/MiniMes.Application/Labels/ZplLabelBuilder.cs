using MiniMes.Domain.Common;

namespace MiniMes.Application.Labels;

// The product label as ZPL (Zebra Programming Language), 60 x 30 mm at 203 dpi = 480 x 240 dots (spec M2 2.5).
// A pure function of its inputs: what a label contains is unit tested as a string, no printer needed.
// Code128 for 1D scanners and the human-readable line; DataMatrix for small boards and 2D imagers.
public static class ZplLabelBuilder
{
    public const int WidthDots = 480;
    public const int HeightDots = 240;
    public const string LabelaryDensity = "8dpmm";   // 203 dpi
    public const string LabelaryInches = "2.36x1.18";   // 60 x 30 mm

    public static string Build(string itemCode, string woNo, string serialNo, int bomVersion, int routingVersion)
    {
        foreach (var value in new[] { itemCode, woNo, serialNo }) RequireSafe(value);

        // ^BCN,60,Y,N,N,A: Code128 60 dots high, text below, mode A = printer picks subset C for digit pairs,
        // which keeps a 17-character serial inside the label width.
        return $"""
            ^XA
            ^PW{WidthDots}
            ^LL{HeightDots}
            ^FO20,15^A0N,26,26^FD{itemCode}   {woNo}^FS
            ^FO20,50^BY2^BCN,60,Y,N,N,A^FD{serialNo}^FS
            ^FO380,140^BXN,4,200^FD{serialNo}^FS
            ^FO20,190^A0N,22,22^FDBOM v{bomVersion}   RT v{routingVersion}^FS
            ^XZ
            """;
    }

    // '^' and '~' start ZPL commands: data containing them would be executed, not printed. The built-in font
    // has no Vietnamese glyphs either, so labels stay printable ASCII.
    private static void RequireSafe(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Any(c => c is '^' or '~' || c < ' ' || c > '~'))
            throw new DomainException($"'{value}' cannot be printed on a label: use printable ASCII without '^' or '~'.");
    }
}
