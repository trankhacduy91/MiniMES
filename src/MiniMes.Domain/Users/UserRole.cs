using MiniMes.Domain.Common;

namespace MiniMes.Domain.Users;

// Audited: "who was granted which role, when" is a typical customer-audit question.
public class UserRole : IAudited
{
    public int UserId { get; private set; }
    public Role Role { get; private set; }

    private UserRole() { }

    internal UserRole(Role role) => Role = role;
}
