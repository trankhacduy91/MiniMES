using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace MiniMes.Infrastructure.Auth;

// Uses JsonWebTokenHandler: the same handler JwtBearer uses to read tokens since .NET 8.
public sealed class JwtTokenService(IOptions<JwtOptions> options, TimeProvider time)
{
    public const string RoleClaim = "role";
    public const string StationClaim = "station";

    public (string Token, DateTime ExpiresAtUtc) CreateToken(
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

        return (new JsonWebTokenHandler().CreateToken(descriptor), expires);
    }
}
