using Microsoft.EntityFrameworkCore;
using MiniMes.Domain.Common;
using MiniMes.Domain.Factory;

namespace MiniMes.Infrastructure.Persistence;

// Demo factory: 1 site, 1 area, 2 lines, 5 stations each (4 in routing + 1 Repair) = 10 stations.
public class DevSeeder(AppDbContext db, TimeProvider time)
{
    private const string SeedUser = "seed";

    public async Task SeedAsync(CancellationToken ct = default)
    {
        if (await db.Sites.AnyAsync(ct)) return; // idempotent

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

        var now = time.GetUtcNow().UtcDateTime;
        foreach (var e in all)
        {
            e.CreatedAt = now;
            e.CreatedBy = SeedUser;
        }

        db.Sites.Add(site);
        await db.SaveChangesAsync(ct);
    }
}
