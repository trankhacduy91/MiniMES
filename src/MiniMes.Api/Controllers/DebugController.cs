using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MiniMes.Application.Common;
using MiniMes.Domain.Common;
using MiniMes.Domain.Factory;
using MiniMes.Domain.Users;
using MiniMes.Infrastructure.Persistence;

namespace MiniMes.Api.Controllers;

// Throwaway endpoints to see error handling (step 4), auth (step 5) and audit (step 6) work.
// Development only; removed once step 7 adds the real Station API (the DbContext here breaks
// the "Api only references Infrastructure for DI" rule on purpose, as a temporary shortcut).
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

    public sealed record RenameStation(string Name);
    public sealed record CreateStation(string LineCode, string Code, string Name);

    [Authorize(Roles = nameof(Role.Admin))]
    [HttpPut("stations/{code}/name")]
    public async Task<IActionResult> RenameStationAsync(
        string code, RenameStation body, [FromServices] AppDbContext db, CancellationToken ct)
    {
        if (!env.IsDevelopment()) return NotFound();
        var station = await db.Stations.SingleOrDefaultAsync(s => s.Code == code, ct)
            ?? throw new KeyNotFoundException($"Station '{code}' not found.");
        station.Rename(body.Name);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    [Authorize(Roles = nameof(Role.Admin))]
    [HttpPost("stations")]
    public async Task<IActionResult> CreateStationAsync(
        CreateStation body, [FromServices] AppDbContext db, CancellationToken ct)
    {
        if (!env.IsDevelopment()) return NotFound();
        var line = await db.Lines.SingleOrDefaultAsync(l => l.Code == body.LineCode, ct)
            ?? throw new KeyNotFoundException($"Line '{body.LineCode}' not found.");
        var station = line.AddStation(body.Code, body.Name, StationType.Smt);
        await db.SaveChangesAsync(ct);
        return Ok(new { station.Id, station.Code });
    }
}
