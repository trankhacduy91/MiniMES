using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MiniMes.Application.Common;
using MiniMes.Domain.Common;
using MiniMes.Domain.Users;

namespace MiniMes.Api.Controllers;

// Throwaway endpoints to see error handling (step 4) and auth (step 5) work. Development only.
[ApiController]
[Route("api/debug")]
public class DebugController(IWebHostEnvironment env, ICurrentUser currentUser) : ControllerBase
{
    [AllowAnonymous]
    [HttpGet("domain-error")]
    public IActionResult DomainError()
        => env.IsDevelopment()
            ? throw new DomainException("Work order in Draft cannot be completed.")
            : NotFound();

    [AllowAnonymous]
    [HttpGet("error")]
    public IActionResult UnexpectedError()
        => env.IsDevelopment()
            ? throw new InvalidOperationException("Sensitive detail: table [dbo].[Secret] missing.")
            : NotFound();

    // Shows the claims exactly as the API sees them: the quickest way to debug "token but always 403".
    [HttpGet("whoami")]
    public IActionResult WhoAmI()
        => env.IsDevelopment()
            ? Ok(new
            {
                currentUser.EmployeeCode,
                currentUser.StationCode,
                currentUser.Roles,
                IsPlanner = User.IsInRole(nameof(Role.Planner)),
                Claims = User.Claims.Select(c => new { c.Type, c.Value }),
            })
            : NotFound();

    [Authorize(Roles = nameof(Role.Admin))]
    [HttpGet("admin-only")]
    public IActionResult AdminOnly() => env.IsDevelopment() ? Ok("admin ok") : NotFound();

    [Authorize(Roles = nameof(Role.Planner) + "," + nameof(Role.Admin))]
    [HttpGet("planner-or-admin")]
    public IActionResult PlannerOrAdmin() => env.IsDevelopment() ? Ok("planner or admin ok") : NotFound();
}
