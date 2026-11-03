using FluentValidation;
using FluentValidation.Results;
using Microsoft.EntityFrameworkCore;
using MiniMes.Domain.Users;

namespace MiniMes.Application.Common;

// A supervisor approves at the kiosk by scanning their badge (the kiosk's token is an operator's): M6 FIFO exception,
// M3b partial carton/pallet. The badge must be an active Supervisor; a wrong one is 400 on the request's field.
internal static class SupervisorBadges
{
    public const string FieldName = "SupervisorBadge";

    public static async Task<string> VerifyAsync(this IAppDbContext db, string badge, CancellationToken ct)
    {
        var code = badge.Trim();
        return await db.Users.Where(u => u.EmployeeCode == code && u.IsActive && u.Roles.Any(r => r.Role == Role.Supervisor))
                   .Select(u => u.EmployeeCode).SingleOrDefaultAsync(ct)   // as stored (the column is case-insensitive)
               ?? throw new ValidationException([new ValidationFailure(FieldName, $"'{code}' is not an active supervisor's badge.")]);
    }
}
