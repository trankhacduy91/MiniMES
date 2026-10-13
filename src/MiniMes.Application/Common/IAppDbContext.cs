using Microsoft.EntityFrameworkCore;
using MiniMes.Domain.MasterData.Boms;
using MiniMes.Domain.MasterData.Calendar;
using MiniMes.Domain.MasterData.DefectCodes;
using MiniMes.Domain.MasterData.Downtime;
using MiniMes.Domain.MasterData.Equipment;
using MiniMes.Domain.MasterData.Items;
using MiniMes.Domain.MasterData.Routings;

namespace MiniMes.Application.Common;

// What use cases may see of the database. Implemented by Infrastructure's AppDbContext.
// Uses EF Core's DbSet (core package only, never the SQL Server provider): handlers write LINQ,
// Infrastructure decides how it reaches SQL Server.
public interface IAppDbContext
{
    DbSet<Item> Items { get; }
    DbSet<Bom> Boms { get; }
    DbSet<Routing> Routings { get; }
    DbSet<WorkCenter> WorkCenters { get; }
    DbSet<Machine> Machines { get; }
    DbSet<Shift> Shifts { get; }
    DbSet<PlantCalendarDay> PlantCalendar { get; }
    DbSet<DefectCode> DefectCodes { get; }
    DbSet<DowntimeReason> DowntimeReasons { get; }

    // A unique-index violation (two requests racing past the "already exists" check) comes out as
    // ConflictException (409), so handlers never see provider-specific SQL errors.
    Task<int> SaveChangesAsync(CancellationToken ct = default);
}
