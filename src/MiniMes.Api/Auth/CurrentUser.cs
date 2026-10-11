using MiniMes.Application.Common;
using MiniMes.Infrastructure.Auth;

namespace MiniMes.Api.Auth;

public sealed class CurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    private System.Security.Claims.ClaimsPrincipal? User => accessor.HttpContext?.User;

    public bool IsAuthenticated => User?.Identity?.IsAuthenticated == true;
    public string? EmployeeCode => User?.FindFirst("sub")?.Value;
    public string? StationCode => User?.FindFirst(JwtTokenService.StationClaim)?.Value;
    public IReadOnlyList<string> Roles =>
        User?.FindAll(JwtTokenService.RoleClaim).Select(c => c.Value).ToList() ?? [];
}
