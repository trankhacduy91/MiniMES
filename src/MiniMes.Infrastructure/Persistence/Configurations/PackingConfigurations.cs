using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MiniMes.Domain.Factory;
using MiniMes.Domain.MasterData.Items;
using MiniMes.Domain.Packing;
using MiniMes.Domain.Planning;

namespace MiniMes.Infrastructure.Persistence.Configurations;

// Spec M3b step 1 / 2.1. Keys are Ids like M6 (user decision): CartonNo/PalletNo are unique label codes, the link
// tables point by CartonId/SerialId. Codes are varchar (no nvarchar parameter traps, M3/M6). Statuses are strings
// so the filtered indexes read as SQL ("[Status] = 'Open'").
public class PackingSpecConfiguration : IEntityTypeConfiguration<PackingSpec>
{
    public void Configure(EntityTypeBuilder<PackingSpec> b)
    {
        b.ToTable("PackingSpecs", t => t.HasCheckConstraint("CK_PackingSpecs_Positive", "[QtyPerCarton] > 0 AND [CartonsPerPallet] > 0"));
        b.HasKey(x => x.ItemId);
        b.Property(x => x.ItemId).ValueGeneratedNever();
        b.HasOne<Item>().WithOne().HasForeignKey<PackingSpec>(x => x.ItemId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class CartonConfiguration : IEntityTypeConfiguration<Carton>
{
    // Carton / pallet numbers (step 2 format): sequences, never MAX + 1 (two stations closing at once, M2 2.4).
    public const string CartonSequence = "CartonSeq", PalletSequence = "PalletSeq";

    public static void AddSequences(ModelBuilder modelBuilder)
    {
        modelBuilder.HasSequence<long>(CartonSequence).StartsAt(1).IncrementsBy(1);
        modelBuilder.HasSequence<long>(PalletSequence).StartsAt(1).IncrementsBy(1);
    }

    public void Configure(EntityTypeBuilder<Carton> b)
    {
        b.ToTable("Cartons", t => t.HasCheckConstraint("CK_Cartons_Qty", "[Qty] >= 0"));
        b.Property(x => x.CartonNo).HasMaxLength(Carton.NumberMaxLength).IsUnicode(false);
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(10).IsUnicode(false);
        b.Property(x => x.ClosedBy).HasMaxLength(50).IsUnicode(false);
        b.Property(x => x.CloseReason).HasMaxLength(Carton.ReasonMaxLength);
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasOne<WorkOrder>().WithMany().HasForeignKey(x => x.WorkOrderId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Item>().WithMany().HasForeignKey(x => x.ItemId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Station>().WithMany().HasForeignKey(x => x.StationId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Pallet>().WithMany().HasForeignKey(x => x.PalletId).OnDelete(DeleteBehavior.Restrict);

        b.HasIndex(x => x.CartonNo).IsUnique();                          // the label code
        // P4: at most one Open carton per (station, WO). Two first scans at once open one carton, the other gets 409.
        b.HasIndex(x => new { x.StationId, x.WorkOrderId }).IsUnique().HasFilter("[Status] = 'Open'")
            .HasDatabaseName("UX_Cartons_OpenPerStationWo");
        b.HasIndex(x => x.PalletId);                                     // cartons of a pallet (ship, derived Hold)
        b.HasIndex(x => new { x.WorkOrderId, x.Status });                // packing report per WO
    }
}

public class CartonSerialConfiguration : IEntityTypeConfiguration<CartonSerial>
{
    public void Configure(EntityTypeBuilder<CartonSerial> b)
    {
        b.ToTable("CartonSerials");
        b.Property(x => x.PackedBy).HasMaxLength(50).IsUnicode(false);
        b.Property(x => x.UnpackedBy).HasMaxLength(50).IsUnicode(false);
        b.Property(x => x.UnpackReason).HasMaxLength(Carton.ReasonMaxLength);
        b.HasOne<Carton>().WithMany().HasForeignKey(x => x.CartonId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Serial>().WithMany().HasForeignKey(x => x.SerialId).OnDelete(DeleteBehavior.Restrict);

        // P3: a serial is in at most one carton at a time; unpacked rows stay as history. Queries for "the carton of
        // serial X" must say UnpackedAt IS NULL to use it (spec 2.1 trap 1).
        b.HasIndex(x => x.SerialId).IsUnique().HasFilter("[UnpackedAt] IS NULL")
            .HasDatabaseName("UX_CartonSerials_LivePerSerial");
        b.HasIndex(x => new { x.CartonId, x.UnpackedAt }).IncludeProperties(x => x.SerialId);   // serials of a carton
    }
}

public class PalletConfiguration : IEntityTypeConfiguration<Pallet>
{
    public void Configure(EntityTypeBuilder<Pallet> b)
    {
        b.ToTable("Pallets", t => t.HasCheckConstraint("CK_Pallets_CartonCount", "[CartonCount] >= 0"));
        b.Property(x => x.PalletNo).HasMaxLength(Pallet.NumberMaxLength).IsUnicode(false);
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(10).IsUnicode(false);
        b.Property(x => x.ShippedBy).HasMaxLength(50).IsUnicode(false);
        b.Property(x => x.ShipmentRef).HasMaxLength(Pallet.ShipmentRefMaxLength).IsUnicode(false);
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasOne<Item>().WithMany().HasForeignKey(x => x.ItemId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Station>().WithMany().HasForeignKey(x => x.StationId).OnDelete(DeleteBehavior.Restrict);

        b.HasIndex(x => x.PalletNo).IsUnique();
        b.HasIndex(x => new { x.StationId, x.ItemId }).IsUnique().HasFilter("[Status] = 'Open'")
            .HasDatabaseName("UX_Pallets_OpenPerStationItem");
    }
}
