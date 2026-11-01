using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using MiniMes.Domain.Factory;
using MiniMes.Domain.Users;

namespace MiniMes.Infrastructure.Persistence;

// Development and Testing data. Users are seeded here, not with HasData: the hash has a random salt,
// so HasData would produce a different migration every time.
// CreatedAt/CreatedBy and AuditLog rows come from the interceptor (ChangedBy = "system").
public class DevSeeder(AppDbContext db, IPasswordHasher<AppUser> hasher)
{
    public const string DemoPassword = "Demo@12345"; // dev only, listed in README

    // M4 step 9: the machine simulator's station role logs in with this badge (POST /api/auth/login-operator), like
    // an operator at a kiosk. No password, so nothing secret sits in machines.json; its scans are recorded under this
    // code with Source = Machine, never under a real worker's name.
    public const string SimulatorBadge = "SIM-STATION";

    // [v2] M5 step 8b: the big screen at the line logs in by itself with this badge (Operator role, read only in
    // practice: the Line TV view has no buttons). Nobody types on a TV.
    public const string LineTvBadge = "LINE-TV";

    public async Task SeedAsync(CancellationToken ct = default)
    {
        await SeedFactoryAsync(ct);
        await SeedUsersAsync(ct);
        await SeedBadgeUserAsync(SimulatorBadge, "Machine simulator", ct);
        await SeedBadgeUserAsync(LineTvBadge, "Line TV", ct);
        await SeedPasswordUserAsync("wh01", "Warehouse One", Role.Warehouse, ct);   // M6 step 2
    }

    // Like SeedBadgeUserAsync, for roles added after the first seed.
    private async Task SeedPasswordUserAsync(string code, string name, Role role, CancellationToken ct)
    {
        if (await db.Users.AnyAsync(u => u.EmployeeCode == code, ct)) return;
        db.Users.Add(WithPassword(new AppUser(code, name), role));
        await db.SaveChangesAsync(ct);
    }

    // Separate from SeedUsersAsync, which stops when any user exists: databases seeded earlier get them too.
    private async Task SeedBadgeUserAsync(string badge, string name, CancellationToken ct)
    {
        if (await db.Users.AnyAsync(u => u.EmployeeCode == badge, ct)) return;
        var user = new AppUser(badge, name);
        user.AddRole(Role.Operator);
        db.Users.Add(user);
        await db.SaveChangesAsync(ct);
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

        db.Users.AddRange(users);
        await db.SaveChangesAsync(ct);
    }

    private AppUser WithPassword(AppUser user, Role role)
    {
        user.SetPasswordHash(hasher.HashPassword(user, DemoPassword));
        user.AddRole(role);
        return user;
    }
}
