namespace MiniMes.Application.Common;

// "Who is calling?" without depending on HTTP. Implemented in Api from HttpContext.User;
// used by use cases and (from step 6) the audit interceptor.
public interface ICurrentUser
{
    bool IsAuthenticated { get; }
    string? EmployeeCode { get; }
    string? StationCode { get; }
    IReadOnlyList<string> Roles { get; }
}
