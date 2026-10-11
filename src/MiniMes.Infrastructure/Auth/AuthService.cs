using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MiniMes.Application.Auth;
using MiniMes.Contracts.Auth;
using MiniMes.Domain.Users;
using MiniMes.Infrastructure.Persistence;

namespace MiniMes.Infrastructure.Auth;

public sealed class AuthService(
    AppDbContext db,
    IPasswordHasher<AppUser> hasher,
    JwtTokenService tokens,
    IOptions<JwtOptions> options,
    ILogger<AuthService> logger) : IAuthService
{
    public async Task<LoginResponse?> LoginAsync(LoginRequest request, CancellationToken ct)
    {
        var user = await FindActiveUserAsync(request.EmployeeCode, ct);

        // Badge-only accounts (no password) can never log in here.
        if (user?.PasswordHash is null
            || hasher.VerifyHashedPassword(user, user.PasswordHash, request.Password) == PasswordVerificationResult.Failed)
        {
            logger.LogWarning("User login failed for {EmployeeCode}", request.EmployeeCode);
            return null;
        }

        var roles = user.Roles.Select(r => r.Role.ToString()).ToList();
        var (token, expires) = tokens.CreateToken(
            user.EmployeeCode, user.FullName, roles, stationCode: null,
            TimeSpan.FromMinutes(options.Value.UserTokenMinutes));

        logger.LogInformation("User {EmployeeCode} logged in with roles {Roles}", user.EmployeeCode, roles);
        return new LoginResponse(token, expires, user.EmployeeCode, user.FullName, roles, null);
    }

    public async Task<LoginResponse?> LoginOperatorAsync(OperatorLoginRequest request, CancellationToken ct)
    {
        var user = await FindActiveUserAsync(request.EmployeeCode, ct);

        // A badge alone must not unlock a password-protected account (e.g. admin).
        if (user is null || user.PasswordHash is not null || !user.HasRole(Role.Operator))
        {
            logger.LogWarning("Operator login failed for {EmployeeCode} at {StationCode}", request.EmployeeCode, request.StationCode);
            return null;
        }

        var station = await db.Stations.AsNoTracking()
            .SingleOrDefaultAsync(s => s.Code == request.StationCode && s.IsActive, ct)
            ?? throw new KeyNotFoundException($"Station '{request.StationCode}' does not exist or is inactive.");

        // Least privilege: a kiosk token only ever carries the Operator role.
        string[] roles = [nameof(Role.Operator)];
        var (token, expires) = tokens.CreateToken(
            user.EmployeeCode, user.FullName, roles, station.Code,
            TimeSpan.FromHours(options.Value.OperatorTokenHours));

        logger.LogInformation("Operator {EmployeeCode} logged in at {StationCode}", user.EmployeeCode, station.Code);
        return new LoginResponse(token, expires, user.EmployeeCode, user.FullName, roles, station.Code);
    }

    private Task<AppUser?> FindActiveUserAsync(string employeeCode, CancellationToken ct)
        => db.Users.AsNoTracking()
            .Include(u => u.Roles)
            .SingleOrDefaultAsync(u => u.EmployeeCode == employeeCode && u.IsActive, ct);
}
