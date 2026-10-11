using Microsoft.EntityFrameworkCore;
using MiniMes.Domain.Factory;

namespace MiniMes.Infrastructure.Persistence;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Site> Sites => Set<Site>();
    public DbSet<Area> Areas => Set<Area>();
    public DbSet<Line> Lines => Set<Line>();
    public DbSet<Station> Stations => Set<Station>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
        => modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
}
