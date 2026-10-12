using MiniMes.Domain.Users;

namespace MiniMes.UnitTests.Domain;

public class AppUserTests
{
    [Fact]
    public void AddRole_twice_keeps_one_role()
    {
        var user = new AppUser("sup01", "Supervisor One");

        user.AddRole(Role.Supervisor);
        user.AddRole(Role.Supervisor);

        Assert.Equal(2, user.Roles.Count); // deliberately wrong: proves CI turns red
    }

    [Fact]
    public void User_can_hold_several_roles()
    {
        var user = new AppUser("sup01", "Supervisor One");

        user.AddRole(Role.Supervisor);
        user.AddRole(Role.QC);

        Assert.True(user.HasRole(Role.Supervisor));
        Assert.True(user.HasRole(Role.QC));
        Assert.False(user.HasRole(Role.Admin));
    }

    [Fact]
    public void New_user_has_no_password_until_one_is_set()
    {
        var user = new AppUser("EMP00123", "Operator");

        Assert.Null(user.PasswordHash);
    }

    [Theory]
    [InlineData("", "Name")]
    [InlineData(" ", "Name")]
    [InlineData("EMP1", "")]
    public void Blank_code_or_name_is_rejected(string code, string name)
        => Assert.ThrowsAny<ArgumentException>(() => new AppUser(code, name));
}
