using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Microsoft.IdentityModel.JsonWebTokens;
using MiniMes.Infrastructure.Auth;
using MiniMes.IntegrationTests.Infrastructure;

namespace MiniMes.IntegrationTests;

// Pure logic, no container. It lives here because the architecture rules forbid
// MiniMes.UnitTests from referencing Infrastructure (CLAUDE.md section 4).
public class JwtTokenServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 1, 0, 0, TimeSpan.Zero);

    private static JwtTokenService CreateService()
        => new(Options.Create(new JwtOptions { Key = ApiFactory.JwtKey }), new FakeTimeProvider(Now));

    [Fact]
    public void Token_contains_sub_name_and_every_role()
    {
        var (token, _) = CreateService().CreateToken("sup01", "Supervisor One", ["Supervisor", "QC"], null, TimeSpan.FromHours(1));

        var jwt = new JsonWebTokenHandler().ReadJsonWebToken(token);
        Assert.Equal("sup01", jwt.Subject);
        Assert.Equal("Supervisor One", jwt.GetClaim("name").Value);
        Assert.Equal(["QC", "Supervisor"], jwt.Claims.Where(c => c.Type == "role").Select(c => c.Value).Order());
        Assert.DoesNotContain(jwt.Claims, c => c.Type == "station");
    }

    [Fact]
    public void Operator_token_has_station_claim()
    {
        var (token, _) = CreateService().CreateToken("EMP00123", "Nam", ["Operator"], "L1-TEST-01", TimeSpan.FromHours(12));

        Assert.Equal("L1-TEST-01", new JsonWebTokenHandler().ReadJsonWebToken(token).GetClaim("station").Value);
    }

    [Fact]
    public void Expiry_is_utc_now_plus_lifetime_from_time_provider()
    {
        var (token, expiresAt) = CreateService().CreateToken("admin", "Admin", ["Admin"], null, TimeSpan.FromMinutes(60));

        Assert.Equal(Now.UtcDateTime.AddMinutes(60), expiresAt);
        Assert.Equal(DateTimeKind.Utc, expiresAt.Kind);
        Assert.Equal(expiresAt, new JsonWebTokenHandler().ReadJsonWebToken(token).ValidTo);
    }
}
