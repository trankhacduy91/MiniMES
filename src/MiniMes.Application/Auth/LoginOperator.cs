using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MiniMes.Application.Common;
using MiniMes.Contracts.Auth;
using MiniMes.Domain.Users;

namespace MiniMes.Application.Auth;

// Kiosk badge scan. null = unknown badge or not an operator badge (401);
// unknown or inactive station -> KeyNotFoundException (404).
public sealed record LoginOperatorCommand(string EmployeeCode, string StationCode) : IRequest<LoginResponse?>;

public sealed class LoginOperatorValidator : AbstractValidator<LoginOperatorCommand>
{
    public LoginOperatorValidator()
    {
        RuleFor(x => x.EmployeeCode).NotEmpty();
        RuleFor(x => x.StationCode).NotEmpty();
    }
}

public sealed class LoginOperatorHandler(IAppDbContext db, ITokenIssuer tokens, ILogger<LoginOperatorHandler> logger)
    : IRequestHandler<LoginOperatorCommand, LoginResponse?>
{
    public async Task<LoginResponse?> Handle(LoginOperatorCommand cmd, CancellationToken ct)
    {
        var user = await db.FindActiveUserAsync(cmd.EmployeeCode, ct);

        // A badge alone must not unlock a password-protected account (e.g. admin).
        if (user is null || user.PasswordHash is not null || !user.HasRole(Role.Operator))
        {
            logger.LogWarning("Operator login failed for {EmployeeCode} at {StationCode}", cmd.EmployeeCode, cmd.StationCode);
            return null;
        }

        var station = await db.Stations.AsNoTracking()
            .SingleOrDefaultAsync(s => s.Code == cmd.StationCode && s.IsActive, ct)
            ?? throw new KeyNotFoundException($"Station '{cmd.StationCode}' does not exist or is inactive.");

        // Least privilege: a kiosk token only ever carries the Operator role.
        var token = tokens.IssueOperatorToken(user.EmployeeCode, user.FullName, station.Code);

        logger.LogInformation("Operator {EmployeeCode} logged in at {StationCode}", user.EmployeeCode, station.Code);
        return new LoginResponse(token.Token, token.ExpiresAtUtc, user.EmployeeCode, user.FullName,
            [nameof(Role.Operator)], station.Code);
    }
}
