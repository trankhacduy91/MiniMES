namespace MiniMes.Domain.Users;

// Stored as text and written into the JWT "role" claim, so names must not change.
public enum Role
{
    Planner,
    Operator,
    Supervisor,
    QC,
    Maintenance,
    Admin,
}
