using Microsoft.AspNetCore.Identity;
using MiniMes.Application.Auth;
using MiniMes.Domain.Users;

namespace MiniMes.Infrastructure.Auth;

// ASP.NET Identity's hasher (PBKDF2, versioned format) stays here so Application never references it.
public sealed class PasswordVerifier(IPasswordHasher<AppUser> hasher) : IPasswordVerifier
{
    public bool Verify(AppUser user, string passwordHash, string password)
        => hasher.VerifyHashedPassword(user, passwordHash, password) != PasswordVerificationResult.Failed;
}
