using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using MiniMes.Application.Common;
using MiniMes.Domain.Audit;
using MiniMes.Domain.Common;
using MiniMes.Domain.Factory;
using MiniMes.Domain.MasterData.Boms;
using MiniMes.Domain.MasterData.Calendar;
using MiniMes.Domain.MasterData.DefectCodes;
using MiniMes.Domain.MasterData.Downtime;
using MiniMes.Domain.MasterData.Equipment;
using MiniMes.Domain.MasterData.Items;
using MiniMes.Domain.MasterData.Routings;
using MiniMes.Domain.Planning;
using MiniMes.Domain.Users;
using MiniMes.Infrastructure.Persistence.Configurations;

namespace MiniMes.Infrastructure.Persistence;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options), IAppDbContext
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

    // M2: work orders and serials
    public DbSet<WorkOrder> WorkOrders => Set<WorkOrder>();
    public DbSet<WorkOrderMaterial> WorkOrderMaterials => Set<WorkOrderMaterial>();
    public DbSet<WorkOrderOperation> WorkOrderOperations => Set<WorkOrderOperation>();
    public DbSet<Serial> Serials => Set<Serial>();

    public Task<IDbContextTransaction> BeginTransactionAsync(CancellationToken ct = default)
        => Database.BeginTransactionAsync(ct);

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
        WorkOrderConfiguration.AddSequences(modelBuilder);
    }

    // The one place that knows SQL Server error numbers: a unique-index violation means two requests
    // raced past the handler's "already exists" check, which is a 409 for the caller, not a 500.
    public override async Task<int> SaveChangesAsync(CancellationToken ct = default)
    {
        try
        {
            return await base.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 })
        {
            throw new ConflictException("This record already exists (it may have just been created by someone else). Reload and try again.");
        }
    }
}
