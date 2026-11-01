using MiniMes.Domain.Common;

namespace MiniMes.Domain.Material;

// L-261101-000017 = "L" + plant date of receipt (yyMMdd) + global sequence (6 digits), 15 characters (user decision,
// M6 step 2): fits LotNo (30) whatever the item code, which can be 50 characters. Only the sequence makes it
// unique (LotSeq, never reset); the label prints item code and supplier lot next to it for people.
// '-' is fine here unlike serials: lot labels are scanned at the kiosk, never typed by a board tester.
public static class LotNumberFormat
{
    public const long MaxSequence = 999_999;
    public const string Prefix = "L-";

    public static string Build(DateOnly plantDate, long sequence)
    {
        if (sequence is < 1 or > MaxSequence)
            throw new DomainException($"Lot sequence {sequence} is out of range 1-{MaxSequence}.");
        return $"{Prefix}{plantDate:yyMMdd}-{sequence:D6}";
    }
}
