using MiniMes.Domain.Common;

namespace MiniMes.Domain.Planning;

// PCBA 261001 000123 2 = model (4) + plant date yyMMdd + sequence (6) + check digit (spec M2 1.2g, 2.4).
// Only the sequence makes it unique (global SerialSeq, never reset); the rest is for people reading the label.
// A pure function, so the format is unit tested without a database.
public static class SerialFormat
{
    public const long MaxSequence = 999_999;   // 6 digits; when exhausted switch to D8 or a sequence per model
    public const int Length = 17;

    public static string Build(string itemCode, DateOnly plantDate, long sequence)
    {
        if (sequence is < 1 or > MaxSequence)
            throw new DomainException($"Serial sequence {sequence} is out of range 1-{MaxSequence}.");

        // No '-': some scanners in keyboard-wedge mode type it wrong depending on the keyboard layout.
        var model = new string(itemCode.Where(char.IsAsciiLetterOrDigit).ToArray()).ToUpperInvariant();
        model = model.Length >= 4 ? model[..4] : model.PadRight(4, 'X');   // PCB-A -> PCBA
        var body = $"{model}{plantDate:yyMMdd}{sequence:D6}";
        return body + CheckDigit(body);
    }

    // M3 checks this before touching the database: a mistyped or smudged serial is "invalid format" at once.
    public static bool HasValidCheckDigit(string serialNo)
        => serialNo.Length == Length && serialNo.All(char.IsAsciiLetterOrDigit)
           && CheckDigit(serialNo[..^1]) == serialNo[^1];

    // ASCII codes weighted 3,1,3,1... ; the digit that brings the sum to a multiple of 10.
    private static char CheckDigit(string body)
    {
        var sum = 0;
        for (var i = 0; i < body.Length; i++) sum += body[i] * (i % 2 == 0 ? 3 : 1);
        return (char)('0' + (10 - sum % 10) % 10);
    }
}
