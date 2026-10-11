namespace MiniMes.Infrastructure.Auth;

public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    public string Issuer { get; init; } = "MiniMes";
    public string Audience { get; init; } = "MiniMes.Clients";
    public string Key { get; init; } = default!;   // secret: user-secrets / env var Jwt__Key, no default
    public int OperatorTokenHours { get; init; } = 12; // about one shift
    public int UserTokenMinutes { get; init; } = 60;   // tokens cannot be revoked, so keep them short
}
