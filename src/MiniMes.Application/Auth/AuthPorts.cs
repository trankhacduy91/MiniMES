using MiniMes.Domain.Users;

namespace MiniMes.Application.Auth;

// The login rules live in Application; how a hash is checked and how a JWT is signed are
// infrastructure details (ASP.NET Identity hasher, JsonWebTokenHandler), hidden behind these two.

public interface IPasswordVerifier
{
    bool Verify(AppUser user, string passwordHash, string password);
}

public interface ITokenIssuer
{
    // BackOffice token: short-lived (tokens cannot be revoked).
    IssuedToken IssueUserToken(string employeeCode, string fullName, IReadOnlyList<string> roles);

    // Kiosk token: about one shift, Operator role only, bound to one station.
    IssuedToken IssueOperatorToken(string employeeCode, string fullName, string stationCode);
}

public sealed record IssuedToken(string Token, DateTime ExpiresAtUtc);
