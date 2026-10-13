using Microsoft.EntityFrameworkCore;
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

namespace MiniMes.Infrastructure.Persistence;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    // M0: factory model, users, audit
    public DbSet<Site> Sites => Set<Site>();
    public DbSet<Area> Areas => Set<Area>();
    public DbSet<Line> Lines => Set<Line>();
    public DbSet<Station> Stations => Set<Station>();
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    // M1: master data
    public DbSet<Item> Items => Set<Item>();
    public DbSet<Bom> Boms => Set<Bom>();
    public DbSet<Routing> Routings => Set<Routing>();
    public DbSet<WorkCenter> WorkCenters => Set<WorkCenter>();
    public DbSet<Machine> Machines => Set<Machine>();
    public DbSet<Shift> Shifts => Set<Shift>();
    public DbSet<PlantCalendarDay> PlantCalendar => Set<PlantCalendarDay>();
    public DbSet<DefectCode> DefectCodes => Set<DefectCode>();
    public DbSet<DowntimeReason> DowntimeReasons => Set<DowntimeReason>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
        => modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
}
