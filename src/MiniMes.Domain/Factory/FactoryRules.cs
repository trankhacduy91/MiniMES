using System.Text.RegularExpressions;
using MiniMes.Domain.Common;

namespace MiniMes.Domain.Factory;

// Codes go into MQTT topics (minimes/{site}/{area}/{line}/...), so no spaces, slashes or '+'/'#'.
// Application validators reuse these constants and answer 400; this is the safety net
// for any other path (seeder, Excel import in M1).
public static partial class FactoryRules
{
    public const string CodePattern = "^[A-Z0-9-]+$";
    public const int CodeMaxLength = 50;
    public const int NameMaxLength = 200;

    [GeneratedRegex(CodePattern)]
    private static partial Regex CodeRegex();

    public static void RequireCode(string code)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        if (code.Length > CodeMaxLength || !CodeRegex().IsMatch(code))
            throw new DomainException($"Code '{code}' must be 1-{CodeMaxLength} characters of A-Z, 0-9 or '-'.");
    }

    public static void RequireName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Length > NameMaxLength)
            throw new DomainException($"Name must be at most {NameMaxLength} characters.");
    }

    public static void RequireActiveParent(bool parentIsActive, string parentKind, string parentCode)
    {
        if (!parentIsActive)
            throw new DomainException($"{parentKind} '{parentCode}' is inactive; cannot add children to it.");
    }
}
