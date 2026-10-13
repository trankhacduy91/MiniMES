using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MiniMes.Domain.MasterData;
using MiniMes.Domain.MasterData.Calendar;

namespace MiniMes.Infrastructure.Persistence.Configurations.MasterData;

public class ShiftConfiguration : IEntityTypeConfiguration<Shift>
{
    public void Configure(EntityTypeBuilder<Shift> b)
    {
        b.ToTable("Shifts");
        b.Property(x => x.Code).HasMaxLength(10);
        b.HasIndex(x => x.Code).IsUnique();
        b.Property(x => x.Name).HasMaxLength(MasterDataRules.NameMaxLength);
        b.Property(x => x.Days).HasConversion<int>(); // [Flags] -> one int column (Mon=1 ... Sun=64)
        b.Property(x => x.CreatedBy).HasMaxLength(100);

        // Owned collection: breaks live and die with their shift, no DbSet of their own.
        b.OwnsMany(x => x.Breaks, o =>
        {
            o.ToTable("ShiftBreaks");
            o.WithOwner().HasForeignKey("ShiftId");
            o.Property<int>("Id");
            o.HasKey("Id");
        });
    }
}

public class PlantCalendarDayConfiguration : IEntityTypeConfiguration<PlantCalendarDay>
{
    public void Configure(EntityTypeBuilder<PlantCalendarDay> b)
    {
        b.ToTable("PlantCalendar");
        b.HasKey(x => x.Date); // one row per date (rule 17)
        b.Property(x => x.ShiftCodes).HasMaxLength(50);
        b.Property(x => x.Note).HasMaxLength(PlantCalendarDay.NoteMaxLength);
        b.Property(x => x.CreatedBy).HasMaxLength(100);
    }
}
