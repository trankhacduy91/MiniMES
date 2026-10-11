namespace MiniMes.Domain.Users;

public class UserRole
{
    public int UserId { get; private set; }
    public Role Role { get; private set; }

    private UserRole() { }

    internal UserRole(Role role) => Role = role;
}
