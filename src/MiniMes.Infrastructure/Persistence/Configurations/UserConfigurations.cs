using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MiniMes.Domain.Users;

namespace MiniMes.Infrastructure.Persistence.Configurations;

public class AppUserConfiguration : IEntityTypeConfiguration<AppUser>
{
    public void Configure(EntityTypeBuilder<AppUser> b)
    {
        b.ToTable("Users");
        b.Property(x => x.EmployeeCode).HasMaxLength(50);
        b.Property(x => x.FullName).HasMaxLength(200);
        b.Property(x => x.PasswordHash).HasMaxLength(256);
        b.Property(x => x.CreatedBy).HasMaxLength(100);
        b.HasIndex(x => x.EmployeeCode).IsUnique();
        b.HasMany(x => x.Roles).WithOne().HasForeignKey(x => x.UserId);
    }
}

public class UserRoleConfiguration : IEntityTypeConfiguration<UserRole>
{
    public void Configure(EntityTypeBuilder<UserRole> b)
    {
        b.ToTable("UserRoles");
        b.HasKey(x => new { x.UserId, x.Role });
        b.Property(x => x.Role).HasConversion<string>().HasMaxLength(20);
    }
}
