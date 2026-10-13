using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MiniMes.Application.Auth;
using MiniMes.Contracts.Auth;

namespace MiniMes.Api.Controllers;

[ApiController]
[Route("api/auth")]
[AllowAnonymous] // the only endpoints (with /health) reachable without a token
public class AuthController(ISender sender) : ControllerBase
{
    // Same message for "unknown code" and "wrong password": do not reveal which accounts exist.
    private const string InvalidCredentials = "Invalid employee code or password.";

    [HttpPost("login")]
    public async Task<ActionResult<LoginResponse>> Login(LoginRequest request, CancellationToken ct)
    {
        var result = await sender.Send(new LoginCommand(request.EmployeeCode, request.Password), ct);
        return result is null
            ? Problem(title: "Login failed", detail: InvalidCredentials, statusCode: StatusCodes.Status401Unauthorized)
            : Ok(result);
    }

    // Unknown/inactive station -> KeyNotFoundException -> 404 via GlobalExceptionHandler.
    [HttpPost("login-operator")]
    public async Task<ActionResult<LoginResponse>> LoginOperator(OperatorLoginRequest request, CancellationToken ct)
    {
        var result = await sender.Send(new LoginOperatorCommand(request.EmployeeCode, request.StationCode), ct);
        return result is null
            ? Problem(title: "Login failed", detail: "Unknown operator badge.", statusCode: StatusCodes.Status401Unauthorized)
            : Ok(result);
    }
}
