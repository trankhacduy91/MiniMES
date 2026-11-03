using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MiniMes.Domain.Factory;
using MiniMes.Domain.Machines;
using MiniMes.Domain.MasterData;
using MiniMes.Domain.MasterData.DefectCodes;
using MiniMes.Domain.MasterData.Items;
using MiniMes.Domain.Planning;
using MiniMes.Domain.Quality;
using MiniMes.Domain.Quality.Fai;

namespace MiniMes.Infrastructure.Persistence.Configurations.Quality;

// Spec M7 step 2, map in docs/quality-erd.md. Keys are Ids like M6/M3b (step 1 decision 1). Values and limits are
// decimal(18,6): specs are compared exactly (Q2), statistics are computed in double elsewhere. Enums are strings
// so the filtered indexes and CHECKs read as SQL ("[Status] = 'Open'"). Big insert-only tables are bigint.
public class InspectionPlanConfiguration : IEntityTypeConfiguration<InspectionPlan>
{
    public void Configure(EntityTypeBuilder<InspectionPlan> b)
    {
        b.ToTable("InspectionPlans", t =>
        {
            t.HasCheckConstraint("CK_InspectionPlans_Limits", "([Usl] IS NOT NULL OR [Lsl] IS NOT NULL) AND ([Lsl] < [Usl] OR [Lsl] IS NULL OR [Usl] IS NULL)");
            // A station plan names item + operation together and the NG code; a machine plan has none of them.
            t.HasCheckConstraint("CK_InspectionPlans_Target", "([ItemId] IS NULL AND [OperationCode] IS NULL) OR " +
                "([ItemId] IS NOT NULL AND [OperationCode] IS NOT NULL AND [DefectCodeId] IS NOT NULL)");
        });
        b.Property(x => x.Code).HasMaxLength(MasterDataRules.CodeMaxLength).IsUnicode(false);
        b.Property(x => x.Name).HasMaxLength(MasterDataRules.NameMaxLength);
        b.Property(x => x.OperationCode).HasMaxLength(MasterDataRules.CodeMaxLength).IsUnicode(false);
        b.Property(x => x.Unit).HasMaxLength(InspectionPlan.UnitMaxLength);          // nvarchar: "°C"
        b.Property(x => x.Frequency).HasMaxLength(InspectionPlan.FrequencyMaxLength);
        b.Property(x => x.CreatedBy).HasMaxLength(100);
        b.Property(x => x.RowVersion).IsRowVersion();
        DecimalColumns(b, x => x.Target, x => x.Usl, x => x.Lsl);
        b.HasOne<Item>().WithMany().HasForeignKey(x => x.ItemId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<DefectCode>().WithMany().HasForeignKey(x => x.DefectCodeId).OnDelete(DeleteBehavior.Restrict);

        b.HasIndex(x => x.Code).IsUnique();
        // The station finds the characteristics of its step: item of the WO + operation code (several per step).
        b.HasIndex(x => new { x.ItemId, x.OperationCode }).HasFilter("[IsActive] = 1 AND [ItemId] IS NOT NULL")
            .HasDatabaseName("IX_InspectionPlans_ItemOperation");
    }

    internal static void DecimalColumns<T>(EntityTypeBuilder<T> b, params System.Linq.Expressions.Expression<Func<T, decimal?>>[] columns)
        where T : class
    {
        foreach (var c in columns) b.Property(c).HasPrecision(18, 6);
    }
}

public class ControlLimitSetConfiguration : IEntityTypeConfiguration<ControlLimitSet>
{
    public void Configure(EntityTypeBuilder<ControlLimitSet> b)
    {
        b.ToTable("ControlLimitSets", t => t.HasCheckConstraint("CK_ControlLimitSets_Sigma", "[Sigma] > 0"));
        foreach (var p in new[] { nameof(ControlLimitSet.Mean), nameof(ControlLimitSet.Sigma), nameof(ControlLimitSet.Ucl),
                     nameof(ControlLimitSet.Lcl), nameof(ControlLimitSet.MrBar), nameof(ControlLimitSet.UclMr) })
            b.Property(p).HasPrecision(18, 6);
        b.Property(x => x.Reason).HasMaxLength(ControlLimitSet.ReasonMaxLength);
        b.Property(x => x.CalculatedBy).HasMaxLength(50).IsUnicode(false);
        b.HasOne<InspectionPlan>().WithMany().HasForeignKey(x => x.InspectionPlanId).OnDelete(DeleteBehavior.Restrict);

        // Q3: one baseline in use per plan; the old ones stay as history. A second "recompute" racing -> 409.
        b.HasIndex(x => x.InspectionPlanId).IsUnique().HasFilter("[IsActive] = 1")
            .HasDatabaseName("UX_ControlLimitSets_ActivePerPlan");
    }
}

public class MeasurementConfiguration : IEntityTypeConfiguration<Measurement>
{
    public void Configure(EntityTypeBuilder<Measurement> b)
    {
        b.ToTable("Measurements", t =>
        {
            // Exactly one source: a machine sample has a tag and its time, never a serial (Q18); the rest is a board.
            t.HasCheckConstraint("CK_Measurements_OneSource",
                "([Source] = 'Machine' AND [MachineTagId] IS NOT NULL AND [SourceTs] IS NOT NULL AND [SerialId] IS NULL AND [StationId] IS NULL) OR " +
                "([Source] <> 'Machine' AND [SerialId] IS NOT NULL AND [StationId] IS NOT NULL AND [MachineTagId] IS NULL)");
            t.HasCheckConstraint("CK_Measurements_Correction",
                "([CorrectsMeasurementId] IS NULL AND [CorrectionReason] IS NULL) OR ([CorrectsMeasurementId] IS NOT NULL AND [CorrectionReason] IS NOT NULL)");
        });
        b.Property(x => x.Source).HasConversion<string>().HasMaxLength(10).IsUnicode(false);
        b.Property(x => x.Result).HasConversion<string>().HasMaxLength(5).IsUnicode(false);
        b.Property(x => x.MeasuredBy).HasMaxLength(Measurement.MeasuredByMaxLength).IsUnicode(false);
        b.Property(x => x.CorrectionReason).HasMaxLength(Measurement.CorrectionReasonMaxLength);
        b.Property(x => x.Value).HasPrecision(18, 6);
        InspectionPlanConfiguration.DecimalColumns(b, x => x.Usl, x => x.Lsl);
        b.Property(x => x.MeasuredAt).HasPrecision(3);
        b.Property(x => x.SourceTs).HasPrecision(3);    // same precision as ProcessValues.SourceTs: the unique key below
        b.HasOne<InspectionPlan>().WithMany().HasForeignKey(x => x.InspectionPlanId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Serial>().WithMany().HasForeignKey(x => x.SerialId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Station>().WithMany().HasForeignKey(x => x.StationId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<MachineTag>().WithMany().HasForeignKey(x => x.MachineTagId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Measurement>().WithMany().HasForeignKey(x => x.CorrectsMeasurementId).OnDelete(DeleteBehavior.Restrict);

        // Spec 2.3: "the N latest points of a plan" (chart, rule engine), ordered by measuring time, Id breaks ties.
        b.HasIndex(x => new { x.InspectionPlanId, x.MeasuredAt, x.Id }).IsDescending(false, true, true)
            .IncludeProperties(x => new { x.Value, x.SerialId, x.Result })
            .HasDatabaseName("IX_Measurements_Plan_MeasuredAt");
        b.HasIndex(x => x.SerialId).HasFilter("[SerialId] IS NOT NULL");                   // FAI, trace of a board
        // 2.3b: the sampler running twice for one machine value stores one point.
        b.HasIndex(x => new { x.MachineTagId, x.SourceTs }).IsUnique().HasFilter("[MachineTagId] IS NOT NULL")
            .HasDatabaseName("UX_Measurements_TagSourceTs");
        // 2.3 trap 3: a station retrying the same request stores one value per characteristic (a request carries
        // several characteristics, all with the request's id: step 5).
        b.HasIndex(x => new { x.ClientRequestId, x.InspectionPlanId }).IsUnique().HasFilter("[ClientRequestId] IS NOT NULL")
            .HasDatabaseName("UX_Measurements_ClientRequestPlan");
        b.HasIndex(x => x.CorrectsMeasurementId).HasFilter("[CorrectsMeasurementId] IS NOT NULL");
    }
}

public class SpcAlertConfiguration : IEntityTypeConfiguration<SpcAlert>
{
    public void Configure(EntityTypeBuilder<SpcAlert> b)
    {
        b.ToTable("SpcAlerts", t => t.HasCheckConstraint("CK_SpcAlerts_Point", "[MeasurementId] IS NOT NULL OR [RuleCode] = 'DataGap'"));
        b.Property(x => x.RuleCode).HasConversion<string>().HasMaxLength(10).IsUnicode(false);
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(10).IsUnicode(false);
        b.Property(x => x.ClosedBy).HasMaxLength(SpcAlert.ClosedByMaxLength).IsUnicode(false);
        b.Property(x => x.CloseNote).HasMaxLength(SpcAlert.CloseNoteMaxLength);
        b.HasOne<InspectionPlan>().WithMany().HasForeignKey(x => x.InspectionPlanId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Measurement>().WithMany().HasForeignKey(x => x.MeasurementId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Ncr>().WithMany().HasForeignKey(x => x.NcrId).OnDelete(DeleteBehavior.Restrict);

        // Q5: one Open alert per (plan, rule). Two stations measuring at once both see "none open": the database
        // keeps one, the other insert fails and is ignored (2.3 trap 2), no HasOpenAsync check to trust.
        b.HasIndex(x => new { x.InspectionPlanId, x.RuleCode }).IsUnique().HasFilter("[Status] = 'Open'")
            .HasDatabaseName("UX_SpcAlerts_OpenPerRule");
        b.HasIndex(x => new { x.InspectionPlanId, x.RaisedAt });                           // alert list next to the chart
    }
}

public class NcrConfiguration : IEntityTypeConfiguration<Ncr>
{
    public const string NcrSequence = "NcrSeq";

    public static void AddSequences(ModelBuilder modelBuilder)
        => modelBuilder.HasSequence<long>(NcrSequence).StartsAt(1).IncrementsBy(1);

    public void Configure(EntityTypeBuilder<Ncr> b)
    {
        b.ToTable("Ncrs", t => t.HasCheckConstraint("CK_Ncrs_Qty", "[Qty] >= 1"));
        b.Property(x => x.NcrNo).HasMaxLength(Ncr.NoMaxLength).IsUnicode(false);
        b.Property(x => x.Source).HasConversion<string>().HasMaxLength(20).IsUnicode(false);
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20).IsUnicode(false);
        b.Property(x => x.Disposition).HasConversion<string>().HasMaxLength(20).IsUnicode(false);
        b.Property(x => x.SourceRef).HasMaxLength(Ncr.RefMaxLength).IsUnicode(false);
        b.Property(x => x.ConcessionRef).HasMaxLength(Ncr.RefMaxLength).IsUnicode(false);
        b.Property(x => x.CapaReference).HasMaxLength(Ncr.RefMaxLength).IsUnicode(false);
        b.Property(x => x.Description).HasMaxLength(Ncr.DescriptionMaxLength);
        b.Property(x => x.DispositionNote).HasMaxLength(Ncr.NoteMaxLength);
        b.Property(x => x.CancelReason).HasMaxLength(Ncr.NoteMaxLength);
        foreach (var by in new[] { nameof(Ncr.ReviewedBy), nameof(Ncr.ProposedBy), nameof(Ncr.ApprovedBy), nameof(Ncr.ClosedBy) })
            b.Property(by).HasMaxLength(50).IsUnicode(false);
        b.Property(x => x.CreatedBy).HasMaxLength(100);
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasOne<DefectCode>().WithMany().HasForeignKey(x => x.DefectCodeId).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(x => x.Items).WithOne().HasForeignKey(i => i.NcrId).OnDelete(DeleteBehavior.Cascade);

        b.HasIndex(x => x.NcrNo).IsUnique();
        b.HasIndex(x => new { x.Status, x.CreatedAt });                                     // NCR list by status
    }
}

public class NcrItemConfiguration : IEntityTypeConfiguration<NcrItem>
{
    public void Configure(EntityTypeBuilder<NcrItem> b)
    {
        b.ToTable("NcrItems");
        b.Property(x => x.TargetType).HasConversion<string>().HasMaxLength(10).IsUnicode(false);
        b.HasIndex(x => new { x.NcrId, x.TargetType, x.TargetId }).IsUnique();
        b.HasIndex(x => new { x.TargetType, x.TargetId });                                 // "NCRs of this serial"
    }
}

public class HoldRecordConfiguration : IEntityTypeConfiguration<HoldRecord>
{
    public void Configure(EntityTypeBuilder<HoldRecord> b)
    {
        b.ToTable("HoldRecords", t => t.HasCheckConstraint("CK_HoldRecords_Release",
            "([ReleasedAt] IS NULL AND [ReleasedBy] IS NULL AND [ReleaseNote] IS NULL) OR " +
            "([ReleasedAt] IS NOT NULL AND [ReleasedBy] IS NOT NULL AND [ReleaseNote] IS NOT NULL)"));
        b.Property(x => x.TargetType).HasConversion<string>().HasMaxLength(10).IsUnicode(false);
        b.Property(x => x.Scope).HasConversion<string>().HasMaxLength(20).IsUnicode(false);
        b.Property(x => x.ScopeRef).HasMaxLength(HoldRecord.ScopeRefMaxLength);
        b.Property(x => x.Reason).HasMaxLength(HoldRecord.ReasonMaxLength);
        b.Property(x => x.ReleaseNote).HasMaxLength(HoldRecord.ReasonMaxLength);
        b.Property(x => x.HeldBy).HasMaxLength(HoldRecord.ByMaxLength).IsUnicode(false);
        b.Property(x => x.ReleasedBy).HasMaxLength(HoldRecord.ByMaxLength).IsUnicode(false);
        b.Ignore(x => x.IsActive);
        // No FK on TargetId: it points at Serials or MaterialLots depending on TargetType (polymorphic).
        b.HasOne<Ncr>().WithMany().HasForeignKey(x => x.NcrId).OnDelete(DeleteBehavior.Restrict);

        // Q8: "does this target still have an active hold?" at every release, set-based.
        b.HasIndex(x => new { x.TargetType, x.TargetId }).HasFilter("[ReleasedAt] IS NULL")
            .HasDatabaseName("IX_HoldRecords_ActiveTarget");
        // One row per target within one Hold action (the INSERT ... SELECT uses DISTINCT; this makes sure).
        b.HasIndex(x => new { x.BatchId, x.TargetType, x.TargetId }).IsUnique();
        b.HasIndex(x => x.NcrId).HasFilter("[NcrId] IS NOT NULL");                          // Q12: holds still open on an NCR
    }
}

public class FaiTemplateConfiguration : IEntityTypeConfiguration<FaiTemplate>
{
    public void Configure(EntityTypeBuilder<FaiTemplate> b)
    {
        b.ToTable("FaiTemplates", t => t.HasCheckConstraint("CK_FaiTemplates_ItemsJson", "ISJSON([ItemsJson]) = 1"));
        b.Property(x => x.CreatedBy).HasMaxLength(100);
        b.HasOne<Item>().WithMany().HasForeignKey(x => x.ItemId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.ItemId, x.Version }).IsUnique();
        b.HasIndex(x => x.ItemId).IsUnique().HasFilter("[IsActive] = 1").HasDatabaseName("UX_FaiTemplates_ActivePerItem");
    }
}

public class FaiRecordConfiguration : IEntityTypeConfiguration<FaiRecord>
{
    public void Configure(EntityTypeBuilder<FaiRecord> b)
    {
        b.ToTable("FaiRecords", t =>
        {
            t.HasCheckConstraint("CK_FaiRecords_PassNeedsAuto", "[Result] <> 'Pass' OR [AutoChecksPassed] = 1");   // Q17
            t.HasCheckConstraint("CK_FaiRecords_ChecklistJson", "ISJSON([ChecklistJson]) = 1");
        });
        b.Property(x => x.Result).HasConversion<string>().HasMaxLength(10).IsUnicode(false);
        b.Property(x => x.Note).HasMaxLength(FaiRecord.NoteMaxLength);
        b.Property(x => x.InspectedBy).HasMaxLength(FaiRecord.ByMaxLength).IsUnicode(false);
        b.HasOne<WorkOrder>().WithMany().HasForeignKey(x => x.WorkOrderId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Serial>().WithMany().HasForeignKey(x => x.SerialId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<FaiTemplate>().WithMany().HasForeignKey(x => x.FaiTemplateId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.WorkOrderId, x.InspectedAt });                              // FAI history of a WO
    }
}
