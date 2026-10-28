using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MiniMes.Domain.MasterData.Calendar;
using MiniMes.Domain.MasterData.Equipment;
using MiniMes.Domain.Oee;

namespace MiniMes.Infrastructure.Persistence.Configurations;

// M5. Computed data, rewritten by the job: not audited (the audit is on the downtime reason that caused a recompute).
public class OeeSnapshotConfiguration : IEntityTypeConfiguration<OeeSnapshot>
{
    public void Configure(EntityTypeBuilder<OeeSnapshot> b)
    {
        b.ToTable("OeeSnapshots");
        b.Property(x => x.WindowStartUtc).HasPrecision(3);
        b.Property(x => x.WindowEndUtc).HasPrecision(3);
        b.Property(x => x.CalculatedAt).HasPrecision(3);
        b.Property(x => x.Warnings).HasConversion<int>();
        b.HasOne<Machine>().WithMany().HasForeignKey(x => x.MachineId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Shift>().WithMany().HasForeignKey(x => x.ShiftId).OnDelete(DeleteBehavior.Restrict);
        // Rule 13: one row per (machine, shift, hour); the job overwrites it. Hour NULL = the shift total: a unique
        // index in SQL Server treats NULLs as equal, so there is exactly one total row per shift too. HasFilter(null):
        // by default EF adds "WHERE [Hour] IS NOT NULL" to a unique index on a nullable column, which would let the
        // total row be inserted twice.
        b.HasIndex(x => new { x.MachineId, x.ShiftDate, x.ShiftId, x.Hour }).IsUnique().HasFilter(null)
            .HasDatabaseName("UX_OeeSnapshots_MachineShiftHour");
        // "What still needs computing" (dirty rows after a reason change).
        b.HasIndex(x => x.IsFinal).HasFilter("[IsFinal] = 0");
    }
}
