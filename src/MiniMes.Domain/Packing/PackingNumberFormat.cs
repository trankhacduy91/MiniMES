using System.Text.RegularExpressions;
using MiniMes.Domain.Common;

namespace MiniMes.Domain.Packing;

// C261103000123 / P261103000004 = prefix + plant date (yyMMdd) + global sequence (6 digits), 13 characters (user
// decision, M3b step 2). The spec's 4 digits would overflow after 9,999 cartons: a SEQUENCE is never reset per day,
// like the lot numbers of M6. No '-': cartons and pallets are scanned at the Pack kiosk, where serials (PCBA...) are
// scanned too, and the shape alone tells the three apart (spec 2.5).
public static partial class PackingNumberFormat
{
    public const long MaxSequence = 999_999;
    public const string CartonPrefix = "C", PalletPrefix = "P";

    public static string Carton(DateOnly plantDate, long sequence) => Build(CartonPrefix, plantDate, sequence);
    public static string Pallet(DateOnly plantDate, long sequence) => Build(PalletPrefix, plantDate, sequence);

    public static bool IsCartonNo(string code) => CartonRegex().IsMatch(code);
    public static bool IsPalletNo(string code) => PalletRegex().IsMatch(code);

    private static string Build(string prefix, DateOnly plantDate, long sequence)
    {
        if (sequence is < 1 or > MaxSequence)
            throw new DomainException($"Packing sequence {sequence} is out of range 1-{MaxSequence}.");
        return $"{prefix}{plantDate:yyMMdd}{sequence:D6}";
    }

    [GeneratedRegex(@"^C\d{12}$")]
    private static partial Regex CartonRegex();

    [GeneratedRegex(@"^P\d{12}$")]
    private static partial Regex PalletRegex();
}
