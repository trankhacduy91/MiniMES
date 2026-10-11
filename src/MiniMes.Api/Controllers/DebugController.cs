using Microsoft.AspNetCore.Mvc;
using MiniMes.Domain.Common;

namespace MiniMes.Api.Controllers;

// Throwaway endpoints to see error handling work (M0 step 4). Development only.
[ApiController]
[Route("api/debug")]
public class DebugController(IWebHostEnvironment env) : ControllerBase
{
    [HttpGet("domain-error")]
    public IActionResult DomainError()
        => env.IsDevelopment()
            ? throw new DomainException("Work order in Draft cannot be completed.")
            : NotFound();

    [HttpGet("error")]
    public IActionResult UnexpectedError()
        => env.IsDevelopment()
            ? throw new InvalidOperationException("Sensitive detail: table [dbo].[Secret] missing.")
            : NotFound();
}
