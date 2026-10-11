using MiniMes.Domain.Common;

namespace MiniMes.Domain.Users;

// One person can hold several roles (e.g. Supervisor + QC).
// Operators have no password: they identify by swiping their badge (EmployeeCode) at a kiosk.
public class AppUser : AuditableEntity
{
    public int Id { get; private set; }
    public string EmployeeCode { get; private set; }
    public string FullName { get; private set; }
    public string? PasswordHash { get; private set; } // only the hash is stored, never the password
    public bool IsActive { get; private set; } = true;

    public List<UserRole> Roles { get; private set; } = [];

    private AppUser() { EmployeeCode = FullName = string.Empty; }

    public AppUser(string employeeCode, string fullName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(employeeCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(fullName);
        EmployeeCode = employeeCode;
        FullName = fullName;
    }

    public void SetPasswordHash(string hash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hash);
        PasswordHash = hash;
    }

    public void AddRole(Role role)
    {
        if (!HasRole(role)) Roles.Add(new UserRole(role));
    }

    public bool HasRole(Role role) => Roles.Any(r => r.Role == role);
}
