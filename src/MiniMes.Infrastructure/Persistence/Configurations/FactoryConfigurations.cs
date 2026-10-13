using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MiniMes.Domain.Factory;
using MiniMes.Domain.MasterData.Equipment;

namespace MiniMes.Infrastructure.Persistence.Configurations;

public class SiteConfiguration : IEntityTypeConfiguration<Site>
{
    public void Configure(EntityTypeBuilder<Site> b)
    {
        b.Property(x => x.Code).HasMaxLength(50);
        b.Property(x => x.Name).HasMaxLength(200);
        b.Property(x => x.CreatedBy).HasMaxLength(100);
        b.HasIndex(x => x.Code).IsUnique();
        b.HasMany(x => x.Areas).WithOne().HasForeignKey(x => x.SiteId);
    }
}

public class AreaConfiguration : IEntityTypeConfiguration<Area>
{
    public void Configure(EntityTypeBuilder<Area> b)
    {
        b.Property(x => x.Code).HasMaxLength(50);
        b.Property(x => x.Name).HasMaxLength(200);
        b.Property(x => x.CreatedBy).HasMaxLength(100);
        b.HasIndex(x => new { x.SiteId, x.Code }).IsUnique();
        b.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.AreaId);
    }
}

public class LineConfiguration : IEntityTypeConfiguration<Line>
{
    public void Configure(EntityTypeBuilder<Line> b)
    {
        b.Property(x => x.Code).HasMaxLength(50);
        b.Property(x => x.Name).HasMaxLength(200);
        b.Property(x => x.CreatedBy).HasMaxLength(100);
        b.HasIndex(x => new { x.AreaId, x.Code }).IsUnique();
        b.HasMany(x => x.Stations).WithOne().HasForeignKey(x => x.LineId);
    }
}

public class StationConfiguration : IEntityTypeConfiguration<Station>
{
    public void Configure(EntityTypeBuilder<Station> b)
    {
        b.Property(x => x.Code).HasMaxLength(50);
        b.Property(x => x.Name).HasMaxLength(200);
        b.Property(x => x.CreatedBy).HasMaxLength(100);
        // Stored as text so SELECT * FROM Stations is readable.
        b.Property(x => x.Type).HasConversion<string>().HasMaxLength(20);
        b.HasIndex(x => x.Code).IsUnique();
        // M1: which work center (routing operation) the station serves; null for Repair. Nullable FK.
        b.HasOne<WorkCenter>().WithMany().HasForeignKey(x => x.WorkCenterId).OnDelete(DeleteBehavior.Restrict);
    }
}
