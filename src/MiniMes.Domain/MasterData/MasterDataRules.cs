using System.Text.RegularExpressions;
using MiniMes.Domain.Common;

namespace MiniMes.Domain.MasterData;

// Shared guards for master data. Codes are business keys printed on labels and matched with ERP,
// so they are upper-case and limited to A-Z 0-9 - _ . (spec M1 1.4 rule 1).
public static partial class MasterDataRules
{
    public const string CodePattern = "^[A-Z0-9._-]+$";
    public const int CodeMaxLength = 50;
    public const int NameMaxLength = 200;

    [GeneratedRegex(CodePattern)]
    private static partial Regex CodeRegex();

    public static void RequireCode(string code, string what)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        if (code.Length > CodeMaxLength || !CodeRegex().IsMatch(code))
            throw new DomainException($"{what} code '{code}' must be 1-{CodeMaxLength} characters of A-Z, 0-9, '-', '_' or '.'.");
    }

    public static void RequireName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Length > NameMaxLength)
            throw new DomainException($"Name must be at most {NameMaxLength} characters.");
    }
}
