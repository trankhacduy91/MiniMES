using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MiniMes.Domain.Factory;
using MiniMes.Domain.MasterData;
using MiniMes.Domain.MasterData.Equipment;
using MiniMes.Domain.MasterData.Items;
using MiniMes.Domain.MasterData.Routings;
using MiniMes.Domain.Planning;

namespace MiniMes.Infrastructure.Persistence.Configurations;

// M2. Quantities use decimal(28,9), the precision of the BOM explosion CTE: 0.0006 kg x 1.05 x 1.01 per board
// must survive the snapshot exactly (rounding happens only on display, ADR-005).
public class WorkOrderConfiguration : IEntityTypeConfiguration<WorkOrder>
{
    public const string WoNoSequence = "WoNoSeq";
    public const string SerialSequence = "SerialSeq";

    // Numbers come from sequences, not MAX + 1: two releases at the same moment still get different numbers.
    // A sequence lives outside transactions, so a rollback leaves a gap; gaps are fine, duplicates are not.
    public static void AddSequences(ModelBuilder modelBuilder)
    {
        modelBuilder.HasSequence<long>(WoNoSequence).StartsAt(1).IncrementsBy(1);
        modelBuilder.HasSequence<long>(SerialSequence).StartsAt(1).IncrementsBy(1);
    }

    public void Configure(EntityTypeBuilder<WorkOrder> b)
    {
        b.ToTable("WorkOrders", t =>
        {
            t.HasCheckConstraint("CK_WorkOrders_PlannedQty", "[PlannedQty] > 0");
            t.HasCheckConstraint("CK_WorkOrders_PlannedWindow", "[PlannedEnd] > [PlannedStart]");
            t.HasCheckConstraint("CK_WorkOrders_Priority", $"[Priority] BETWEEN {WorkOrder.MinPriority} AND {WorkOrder.MaxPriority}");
        });
        b.HasIndex(x => x.WoNo).IsUnique();
        // [v2] dispatch list: WOs of a line in a given status, by priority then planned start.
        b.HasIndex(x => new { x.LineId, x.Status, x.Priority, x.PlannedStart });

        b.Property(x => x.WoNo).HasMaxLength(WorkOrder.WoNoMaxLength).IsUnicode(false);
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.FaiStatus).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.FaiSerialNo).HasMaxLength(Serial.SerialNoMaxLength).IsUnicode(false);
        b.Property(x => x.HoldReason).HasMaxLength(WorkOrder.ReasonMaxLength);
        b.Property(x => x.CompleteReason).HasMaxLength(WorkOrder.ReasonMaxLength);
        b.Property(x => x.CancelReason).HasMaxLength(WorkOrder.ReasonMaxLength);
        b.Property(x => x.ExternalRef).HasMaxLength(50);   // M8; nullable, nothing reads it yet
        b.Property(x => x.CreatedBy).HasMaxLength(100);
        // SQL Server rowversion: EF adds "AND RowVersion = @original" to every UPDATE; 0 rows -> concurrency error.
        b.Property(x => x.RowVersion).IsRowVersion();

        b.HasOne<Item>().WithMany().HasForeignKey(x => x.ItemId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Line>().WithMany().HasForeignKey(x => x.LineId).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(x => x.Materials).WithOne().HasForeignKey(x => x.WorkOrderId).OnDelete(DeleteBehavior.Cascade);
        b.HasMany(x => x.Operations).WithOne().HasForeignKey(x => x.WorkOrderId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class WorkOrderMaterialConfiguration : IEntityTypeConfiguration<WorkOrderMaterial>
{
    public void Configure(EntityTypeBuilder<WorkOrderMaterial> b)
    {
        b.ToTable("WorkOrderMaterials", t => t.HasCheckConstraint("CK_WorkOrderMaterials_QtyPerUnit", "[QtyPerUnit] > 0"));
        // The same component may be used at two steps; one row per (component, step).
        b.HasIndex(x => new { x.WorkOrderId, x.ComponentItemId, x.OperationSeq }).IsUnique();
        b.Property(x => x.QtyPerUnit).HasPrecision(28, 9);
        b.Property(x => x.RequiredQty).HasPrecision(28, 9);
        b.Property(x => x.IssuedQty).HasPrecision(28, 9);
        b.Property(x => x.ConsumedQty).HasPrecision(28, 9);
        b.HasOne<Item>().WithMany().HasForeignKey(x => x.ComponentItemId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class WorkOrderOperationConfiguration : IEntityTypeConfiguration<WorkOrderOperation>
{
    public void Configure(EntityTypeBuilder<WorkOrderOperation> b)
    {
        b.ToTable("WorkOrderOperations", t =>
        {
            t.HasCheckConstraint("CK_WorkOrderOperations_IdealCycleTime", "[IdealCycleTimeSec] > 0");
            t.HasCheckConstraint("CK_WorkOrderOperations_Counters", "[QtyGood] >= 0 AND [QtyNg] >= 0");
        });
        b.HasIndex(x => new { x.WorkOrderId, x.Seq }).IsUnique();
        b.Property(x => x.OperationCode).HasMaxLength(MasterDataRules.CodeMaxLength);
        b.Property(x => x.IdealCycleTimeSec).HasPrecision(9, 3);   // same as RoutingStep
        b.Property(x => x.SopUrl).HasMaxLength(RoutingStep.SopUrlMaxLength);
        b.HasOne<WorkCenter>().WithMany().HasForeignKey(x => x.WorkCenterId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class SerialConfiguration : IEntityTypeConfiguration<Serial>
{
    public void Configure(EntityTypeBuilder<Serial> b)
    {
        b.ToTable("Serials");
        // Second line of defence after the sequence: a bug that builds the same text twice is refused here.
        b.HasIndex(x => x.SerialNo).IsUnique();
        // Progress and WIP per WO; CurrentSeq included so WIP-by-step (M3) reads the index only.
        b.HasIndex(x => new { x.WorkOrderId, x.Status }).IncludeProperties(x => x.CurrentSeq);
        b.Property(x => x.RowVersion).IsRowVersion();
        b.Property(x => x.SerialNo).HasMaxLength(Serial.SerialNoMaxLength).IsUnicode(false);
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.ScrapReason).HasMaxLength(Serial.ScrapReasonMaxLength);
        // Restrict, not cascade: a serial is traceability history and must never disappear with its WO.
        b.HasOne<WorkOrder>().WithMany().HasForeignKey(x => x.WorkOrderId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Item>().WithMany().HasForeignKey(x => x.ItemId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class LabelPrintLogConfiguration : IEntityTypeConfiguration<LabelPrintLog>
{
    public void Configure(EntityTypeBuilder<LabelPrintLog> b)
    {
        b.ToTable("LabelPrintLogs", t => t.HasCheckConstraint("CK_LabelPrintLogs_PrintNo", "[PrintNo] >= 1"));
        // One number per print of a serial: two reprints racing for "PrintNo 3" -> one of them gets 409.
        b.HasIndex(x => new { x.SerialId, x.PrintNo }).IsUnique();
        b.HasIndex(x => x.WorkOrderId);
        b.Property(x => x.SerialNo).HasMaxLength(Serial.SerialNoMaxLength).IsUnicode(false);
        b.Property(x => x.PrintedBy).HasMaxLength(100);
        b.Property(x => x.Reason).HasMaxLength(LabelPrintLog.ReasonMaxLength);
        b.HasOne<Serial>().WithMany().HasForeignKey(x => x.SerialId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<WorkOrder>().WithMany().HasForeignKey(x => x.WorkOrderId).OnDelete(DeleteBehavior.Restrict);
    }
}
