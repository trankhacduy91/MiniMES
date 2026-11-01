using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MiniMes.Domain.Factory;
using MiniMes.Domain.Material;
using MiniMes.Domain.MasterData;
using MiniMes.Domain.MasterData.Items;
using MiniMes.Domain.Planning;

namespace MiniMes.Infrastructure.Persistence.Configurations;

// Spec M6 step 1 / 2.3: one covering index per trace path. Codes are varchar (A-Z, digits, '-'): an nvarchar
// parameter against a varchar column would force CONVERT_IMPLICIT and a scan (same rule as ScanLogs).
public class MaterialLotConfiguration : IEntityTypeConfiguration<MaterialLot>
{
    public void Configure(EntityTypeBuilder<MaterialLot> b)
    {
        b.ToTable("MaterialLots", t =>
        {
            // R10: theoretical stock is never negative and never above what was received.
            t.HasCheckConstraint("CK_MaterialLots_Qty", "[Qty] > 0 AND [RemainingQty] >= 0 AND [RemainingQty] <= [Qty]");
        });
        b.Property(x => x.LotNo).HasMaxLength(MaterialLot.LotNoMaxLength).IsUnicode(false);
        b.Property(x => x.SupplierLot).HasMaxLength(MaterialLot.SupplierLotMaxLength).IsUnicode(false);
        b.Property(x => x.Qty).HasPrecision(18, 3);
        b.Property(x => x.RemainingQty).HasPrecision(18, 3);
        b.Property(x => x.Status).HasConversion<byte>();
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasOne<Item>().WithMany().HasForeignKey(x => x.ItemId).OnDelete(DeleteBehavior.Restrict);

        b.HasIndex(x => x.LotNo).IsUnique();                                           // R1: the label code
        b.HasIndex(x => new { x.SupplierLot, x.ItemId });                              // forward trace from a supplier lot, IQC
        b.HasIndex(x => new { x.ItemId, x.Status, x.ReceivedAt })                      // FIFO candidates
            .IncludeProperties(x => new { x.RemainingQty, x.ExpiryAt });
    }
}

public class MaterialLoadConfiguration : IEntityTypeConfiguration<MaterialLoad>
{
    public void Configure(EntityTypeBuilder<MaterialLoad> b)
    {
        b.ToTable("MaterialLoads");
        b.Property(x => x.SlotNo).HasMaxLength(MaterialLoad.SlotNoMaxLength).IsUnicode(false);
        b.Property(x => x.LoadedBy).HasMaxLength(50).IsUnicode(false);
        b.Property(x => x.UnloadedBy).HasMaxLength(50).IsUnicode(false);
        b.Property(x => x.FifoOverrideBy).HasMaxLength(50).IsUnicode(false);
        b.Property(x => x.FifoOverrideReason).HasMaxLength(MaterialLoad.ReasonMaxLength);
        b.HasOne<Station>().WithMany().HasForeignKey(x => x.StationId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Item>().WithMany().HasForeignKey(x => x.ItemId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<MaterialLot>().WithMany().HasForeignKey(x => x.LotId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<WorkOrder>().WithMany().HasForeignKey(x => x.WorkOrderId).OnDelete(DeleteBehavior.Restrict);

        // R6 in the database: at most one lot on (station, item) at a time. Two kiosks loading at once -> one 409.
        // It is also the seek of every OK scan ("which lot is on this station for this item").
        b.HasIndex(x => new { x.StationId, x.ItemId }).IsUnique().HasFilter("[UnloadedAt] IS NULL")
            .HasDatabaseName("UX_MaterialLoads_ActivePerStationItem")
            .IncludeProperties(x => x.LotId);
        b.HasIndex(x => new { x.LotId, x.LoadedAt });                                  // trace: who loaded this lot, where
    }
}

// Insert-only (R11): a trigger refuses UPDATE/DELETE (migration). No FK to Serial/Station: like ScanLogs, the
// hottest insert of the system skips FK checks and the evidence survives master data changes. The FK to the lot
// stays: a component pointing at no lot would be a broken trace.
public class SerialComponentConfiguration : IEntityTypeConfiguration<SerialComponent>
{
    public void Configure(EntityTypeBuilder<SerialComponent> b)
    {
        b.ToTable("SerialComponents");
        b.Property(x => x.Qty).HasPrecision(18, 3);
        b.HasOne<MaterialLot>().WithMany().HasForeignKey(x => x.LotId).OnDelete(DeleteBehavior.Restrict);

        // R9 + backward trace (serial -> lots): seek on SerialId.
        b.HasIndex(x => new { x.SerialId, x.StationId, x.ItemId, x.LotId }).IsUnique()
            .HasDatabaseName("UX_SerialComponents_SerialStationItemLot");
        // Forward trace (lot -> serials): ~2,500 rows per reel, no key lookup thanks to INCLUDE.
        b.HasIndex(x => x.LotId).IncludeProperties(x => x.SerialId);
    }
}

public class LotGenealogyConfiguration : IEntityTypeConfiguration<LotGenealogy>
{
    public void Configure(EntityTypeBuilder<LotGenealogy> b)
    {
        b.ToTable("LotGenealogy", t => t.HasCheckConstraint("CK_LotGenealogy_NotSelf", "[ParentLotId] <> [ChildLotId]"));
        b.Property(x => x.Qty).HasPrecision(18, 3);
        b.HasOne<MaterialLot>().WithMany().HasForeignKey(x => x.ParentLotId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<MaterialLot>().WithMany().HasForeignKey(x => x.ChildLotId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.ParentLotId, x.ChildLotId }).IsUnique();               // forward recursion seeks on Parent
        b.HasIndex(x => x.ChildLotId).IncludeProperties(x => new { x.ParentLotId, x.Qty });   // backward recursion
    }
}

public class MaterialVerificationLogConfiguration : IEntityTypeConfiguration<MaterialVerificationLog>
{
    public void Configure(EntityTypeBuilder<MaterialVerificationLog> b)
    {
        b.ToTable("MaterialVerificationLogs");
        b.Property(x => x.ScannedLotNo).HasMaxLength(MaterialLot.LotNoMaxLength).IsUnicode(false);
        b.Property(x => x.SuggestedLotNo).HasMaxLength(MaterialLot.LotNoMaxLength).IsUnicode(false);
        b.Property(x => x.Outcome).HasConversion<string>().HasMaxLength(10).IsUnicode(false);
        b.Property(x => x.Code).HasConversion<string>().HasMaxLength(30).IsUnicode(false);
        b.Property(x => x.By).HasMaxLength(50).IsUnicode(false);
        b.Property(x => x.OverrideBy).HasMaxLength(50).IsUnicode(false);
        b.Property(x => x.OverrideReason).HasMaxLength(MaterialLoad.ReasonMaxLength);
        b.HasIndex(x => new { x.StationId, x.At });                                    // "what was refused on this station"
    }
}

public class IqcRecordConfiguration : IEntityTypeConfiguration<IqcRecord>
{
    public void Configure(EntityTypeBuilder<IqcRecord> b)
    {
        b.ToTable("IqcRecords");
        b.Property(x => x.SupplierLot).HasMaxLength(MaterialLot.SupplierLotMaxLength).IsUnicode(false);
        b.Property(x => x.Result).HasConversion<string>().HasMaxLength(10).IsUnicode(false);
        b.Property(x => x.DefectCode).HasMaxLength(MasterDataRules.CodeMaxLength).IsUnicode(false);
        b.Property(x => x.Note).HasMaxLength(IqcRecord.NoteMaxLength);
        b.Property(x => x.InspectedBy).HasMaxLength(50).IsUnicode(false);
        b.HasOne<Item>().WithMany().HasForeignKey(x => x.ItemId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.ItemId, x.SupplierLot });
    }
}
