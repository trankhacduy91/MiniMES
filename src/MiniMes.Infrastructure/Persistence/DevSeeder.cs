using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using MiniMes.Domain.Common;
using MiniMes.Domain.Factory;
using MiniMes.Domain.Users;

namespace MiniMes.Infrastructure.Persistence;

// Development data. Users are seeded here, not with HasData: the hash has a random salt,
// so HasData would produce a different migration every time.
public class DevSeeder(AppDbContext db, TimeProvider time, IPasswordHasher<AppUser> hasher)
{
    private const string SeedUser = "seed";
    public const string DemoPassword = "Demo@12345"; // dev only, listed in README

    public async Task SeedAsync(CancellationToken ct = default)
    {
        await SeedFactoryAsync(ct);
        await SeedUsersAsync(ct);
    }

    // 1 site, 1 area, 2 lines, 5 stations each (4 in routing + 1 Repair) = 10 stations.
    private async Task SeedFactoryAsync(CancellationToken ct)
    {
        if (await db.Sites.AnyAsync(ct)) return;

        var site = new Site("VN-BN01", "Bac Ninh Factory");
        var area = site.AddArea("SMT-ASSY", "SMT and Assembly");
        foreach (var lineCode in new[] { "L1", "L2" })
        {
            var line = area.AddLine(lineCode, $"Line {lineCode[1..]}");
            line.AddStation($"{lineCode}-SMT-01", "SMT", StationType.Smt);
            line.AddStation($"{lineCode}-ASM-01", "Assembly", StationType.Assembly);
            line.AddStation($"{lineCode}-TEST-01", "Test", StationType.Test);
            line.AddStation($"{lineCode}-PACK-01", "Pack", StationType.Pack);
            line.AddStation($"{lineCode}-REPAIR", "Repair", StationType.Repair);
        }

        var lines = site.Areas.SelectMany(a => a.Lines).ToList();
        var all = new List<AuditableEntity> { site };
        all.AddRange(site.Areas);
        all.AddRange(lines);
        all.AddRange(lines.SelectMany(l => l.Stations));
        Stamp(all);

        db.Sites.Add(site);
        await db.SaveChangesAsync(ct);
    }

    private async Task SeedUsersAsync(CancellationToken ct)
    {
        if (await db.Users.AnyAsync(ct)) return;

        var users = new List<AppUser>
        {
            WithPassword(new AppUser("admin", "System Admin"), Role.Admin),
            WithPassword(new AppUser("planner01", "Planner One"), Role.Planner),
            WithPassword(new AppUser("sup01", "Supervisor One"), Role.Supervisor),
            WithPassword(new AppUser("qc01", "QC One"), Role.QC),
            WithPassword(new AppUser("mt01", "Maintenance One"), Role.Maintenance),
        };

        var operatorUser = new AppUser("EMP00123", "Nguyen Van Nam"); // badge only, no password
        operatorUser.AddRole(Role.Operator);
        users.Add(operatorUser);

        Stamp(users);
        db.Users.AddRange(users);
        await db.SaveChangesAsync(ct);
    }

    private AppUser WithPassword(AppUser user, Role role)
    {
        user.SetPasswordHash(hasher.HashPassword(user, DemoPassword));
        user.AddRole(role);
        return user;
    }

    private void Stamp(IEnumerable<AuditableEntity> entities)
    {
        var now = time.GetUtcNow().UtcDateTime;
        foreach (var e in entities)
        {
            e.CreatedAt = now;
            e.CreatedBy = SeedUser;
        }
    }
}
