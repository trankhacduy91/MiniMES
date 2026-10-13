using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using MiniMes.Application.Auth;
using MiniMes.Domain.Users;

namespace MiniMes.Infrastructure.Auth;

// Uses JsonWebTokenHandler: the same handler JwtBearer uses to read tokens since .NET 8.
public sealed class JwtTokenService(IOptions<JwtOptions> options, TimeProvider time) : ITokenIssuer
{
    public const string RoleClaim = "role";
    public const string StationClaim = "station";

    public IssuedToken IssueUserToken(string employeeCode, string fullName, IReadOnlyList<string> roles)
        => CreateToken(employeeCode, fullName, roles, stationCode: null,
            TimeSpan.FromMinutes(options.Value.UserTokenMinutes));

    public IssuedToken IssueOperatorToken(string employeeCode, string fullName, string stationCode)
        => CreateToken(employeeCode, fullName, [nameof(Role.Operator)], stationCode,
            TimeSpan.FromHours(options.Value.OperatorTokenHours));

    public IssuedToken CreateToken(
        string employeeCode, string fullName, IEnumerable<string> roles, string? stationCode, TimeSpan lifetime)
    {
        var o = options.Value;
        var now = time.GetUtcNow().UtcDateTime;
        var expires = now.Add(lifetime);

        var claims = new List<Claim> { new("sub", employeeCode), new("name", fullName) };
        claims.AddRange(roles.Select(r => new Claim(RoleClaim, r)));
        if (stationCode is not null) claims.Add(new Claim(StationClaim, stationCode));

        var descriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(claims),
            Issuer = o.Issuer,
            Audience = o.Audience,
            IssuedAt = now,
            NotBefore = now,
            Expires = expires,
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(o.Key)), SecurityAlgorithms.HmacSha256),
        };

        return new IssuedToken(new JsonWebTokenHandler().CreateToken(descriptor), expires);
    }
}
