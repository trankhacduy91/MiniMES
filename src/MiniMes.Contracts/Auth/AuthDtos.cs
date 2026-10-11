namespace MiniMes.Contracts.Auth;

// BackOffice: employee code + password.
public sealed record LoginRequest(string EmployeeCode, string Password);

// Kiosk: badge scan only; StationCode comes from the kiosk's appsettings, not from the operator.
public sealed record OperatorLoginRequest(string EmployeeCode, string StationCode);

public sealed record LoginResponse(
    string Token,
    DateTime ExpiresAtUtc,
    string EmployeeCode,
    string FullName,
    IReadOnlyList<string> Roles,
    string? StationCode);
