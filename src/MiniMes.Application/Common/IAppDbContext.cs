using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using MiniMes.Domain.Audit;
using MiniMes.Domain.Factory;
using MiniMes.Domain.MasterData.Boms;
using MiniMes.Domain.MasterData.Calendar;
using MiniMes.Domain.MasterData.DefectCodes;
using MiniMes.Domain.MasterData.Downtime;
using MiniMes.Domain.MasterData.Equipment;
using MiniMes.Domain.MasterData.Items;
using MiniMes.Domain.MasterData.Routings;
using MiniMes.Domain.Users;

namespace MiniMes.Application.Common;

// What use cases may see of the database. Implemented by Infrastructure's AppDbContext.
// Uses EF Core's DbSet (core package only, never the SQL Server provider): handlers write LINQ,
// Infrastructure decides how it reaches SQL Server.
public interface IAppDbContext
{
    // M0: factory model, users, audit
    DbSet<Site> Sites { get; }
    DbSet<Area> Areas { get; }
    DbSet<Line> Lines { get; }
    DbSet<Station> Stations { get; }
    DbSet<AppUser> Users { get; }
    DbSet<AuditLog> AuditLogs { get; }

    // M1: master data
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

    // For the few use cases that must save more than once atomically (the Excel import needs item ids
    // before it can write BOM lines). The audit interceptor joins this transaction instead of opening one.
    Task<IDbContextTransaction> BeginTransactionAsync(CancellationToken ct = default);
}
