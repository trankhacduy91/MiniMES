using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MiniMes.Domain.Execution;
using MiniMes.Domain.Factory;
using MiniMes.Domain.MasterData;
using MiniMes.Domain.Planning;

namespace MiniMes.Infrastructure.Persistence.Configurations;

// M3. ScanLog is the big table (millions of rows): narrow varchar columns, and indexes chosen for the hot path.
public class ScanLogConfiguration : IEntityTypeConfiguration<ScanLog>
{
    public void Configure(EntityTypeBuilder<ScanLog> b)
    {
        b.ToTable("ScanLogs");
        // Route check reads "last scans of this serial" on every scan (duplicate window, FPY first pass).
        // varchar, not nvarchar: an nvarchar parameter against a varchar column forces CONVERT_IMPLICIT and a scan.
        b.HasIndex(x => new { x.SerialNo, x.ScannedAt });
        b.HasIndex(x => x.WorkOrderId);   // reports per WO
        b.Property(x => x.SerialNo).HasMaxLength(Serial.SerialNoMaxLength).IsUnicode(false);
        b.Property(x => x.Result).HasConversion<string>().HasMaxLength(10).IsUnicode(false);
        b.Property(x => x.Source).HasConversion<string>().HasMaxLength(10).IsUnicode(false);
        b.Property(x => x.DefectCode).HasMaxLength(MasterDataRules.CodeMaxLength).IsUnicode(false);
        b.Property(x => x.OperatorCode).HasMaxLength(50).IsUnicode(false);
        // No FK to Serial/WorkOrder/Station: an insert-only log keeps its rows even if master data changes,
        // and each FK check would cost time on the hottest INSERT of the system.
    }
}

public class RepairLogConfiguration : IEntityTypeConfiguration<RepairLog>
{
    public void Configure(EntityTypeBuilder<RepairLog> b)
    {
        b.ToTable("RepairLogs");
        b.HasIndex(x => new { x.SerialNo, x.RepairedAt });
        b.Property(x => x.SerialNo).HasMaxLength(Serial.SerialNoMaxLength).IsUnicode(false);
        b.Property(x => x.DefectCode).HasMaxLength(MasterDataRules.CodeMaxLength).IsUnicode(false);
        b.Property(x => x.Action).HasMaxLength(RepairLog.ActionMaxLength);
        b.Property(x => x.ComponentReplaced).HasMaxLength(RepairLog.ComponentMaxLength);
        b.Property(x => x.RepairedBy).HasMaxLength(50).IsUnicode(false);
        b.HasOne<Serial>().WithMany().HasForeignKey(x => x.SerialId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class StationSessionConfiguration : IEntityTypeConfiguration<StationSession>
{
    public void Configure(EntityTypeBuilder<StationSession> b)
    {
        b.ToTable("StationSessions");
        // Rule: at most one open session per station, even if two kiosks log in at the same moment.
        b.HasIndex(x => x.StationId, "UX_StationSessions_OneOpenPerStation").IsUnique().HasFilter("[ClosedAt] IS NULL");
        b.Property(x => x.OperatorCode).HasMaxLength(50).IsUnicode(false);
        b.HasOne<Station>().WithMany().HasForeignKey(x => x.StationId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<WorkOrder>().WithMany().HasForeignKey(x => x.WorkOrderId).OnDelete(DeleteBehavior.Restrict);
    }
}
