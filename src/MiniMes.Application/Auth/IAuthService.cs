using MiniMes.Contracts.Auth;

namespace MiniMes.Application.Auth;

// M0 uses a plain service; from M1 every use case goes through MediatR.
public interface IAuthService
{
    /// <returns>null when the code/password pair is wrong (caller answers 401).</returns>
    Task<LoginResponse?> LoginAsync(LoginRequest request, CancellationToken ct);

    /// <returns>null when the badge is unknown or not an operator badge.</returns>
    /// <exception cref="KeyNotFoundException">Station does not exist or is inactive.</exception>
    Task<LoginResponse?> LoginOperatorAsync(OperatorLoginRequest request, CancellationToken ct);
}
