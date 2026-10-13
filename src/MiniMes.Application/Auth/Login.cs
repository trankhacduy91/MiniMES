using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MiniMes.Application.Common;
using MiniMes.Contracts.Auth;
using MiniMes.Domain.Users;

namespace MiniMes.Application.Auth;

// null = wrong code/password; the controller answers 401 with one message for both cases.
public sealed record LoginCommand(string EmployeeCode, string Password) : IRequest<LoginResponse?>;

public sealed class LoginValidator : AbstractValidator<LoginCommand>
{
    public LoginValidator()
    {
        RuleFor(x => x.EmployeeCode).NotEmpty();
        RuleFor(x => x.Password).NotEmpty();
    }
}

public sealed class LoginHandler(IAppDbContext db, IPasswordVerifier passwords, ITokenIssuer tokens, ILogger<LoginHandler> logger)
    : IRequestHandler<LoginCommand, LoginResponse?>
{
    public async Task<LoginResponse?> Handle(LoginCommand cmd, CancellationToken ct)
    {
        var user = await db.FindActiveUserAsync(cmd.EmployeeCode, ct);

        // Badge-only accounts (no password) can never log in here.
        if (user?.PasswordHash is null || !passwords.Verify(user, user.PasswordHash, cmd.Password))
        {
            logger.LogWarning("User login failed for {EmployeeCode}", cmd.EmployeeCode);
            return null;
        }

        var roles = user.Roles.Select(r => r.Role.ToString()).ToList();
        var token = tokens.IssueUserToken(user.EmployeeCode, user.FullName, roles);

        logger.LogInformation("User {EmployeeCode} logged in with roles {Roles}", user.EmployeeCode, roles);
        return new LoginResponse(token.Token, token.ExpiresAtUtc, user.EmployeeCode, user.FullName, roles, null);
    }
}

internal static class UserQueries
{
    public static Task<AppUser?> FindActiveUserAsync(this IAppDbContext db, string employeeCode, CancellationToken ct)
        => db.Users.AsNoTracking()
            .Include(u => u.Roles)
            .SingleOrDefaultAsync(u => u.EmployeeCode == employeeCode && u.IsActive, ct);
}
