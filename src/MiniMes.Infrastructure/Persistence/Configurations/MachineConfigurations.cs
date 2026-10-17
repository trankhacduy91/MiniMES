using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MiniMes.Domain.Andon;
using MiniMes.Domain.Machines;
using MiniMes.Domain.MasterData.Downtime;
using MiniMes.Domain.MasterData.Equipment;

namespace MiniMes.Infrastructure.Persistence.Configurations;

// M4. MachineEvent and ProcessValue are the high-volume tables: tinyint enums, datetime2(3), narrow indexes.
// No FK on them (insert-only logs, like ScanLogs): each FK check would cost time on every batch insert.
public class MachineEventConfiguration : IEntityTypeConfiguration<MachineEvent>
{
    public void Configure(EntityTypeBuilder<MachineEvent> b)
    {
        b.ToTable("MachineEvents");
        b.Property(x => x.Kind).HasConversion<byte>();
        b.Property(x => x.State).HasConversion<byte?>();
        b.Property(x => x.MachineTs).HasPrecision(3);
        b.Property(x => x.ReceivedAt).HasPrecision(3);
        b.Property(x => x.AlarmCode).HasMaxLength(MachineEvent.AlarmCodeMaxLength).IsUnicode(false);
        // QoS 1 = at least once: the same message may arrive twice, the second insert fails here.
        b.HasIndex(x => x.MsgId).IsUnique();
        // OEE (M5) sums Delta per machine and time range.
        b.HasIndex(x => new { x.MachineId, x.MachineTs }).IncludeProperties(x => new { x.Kind, x.Delta });
    }
}

public class MachineStateHistoryConfiguration : IEntityTypeConfiguration<MachineStateHistory>
{
    public void Configure(EntityTypeBuilder<MachineStateHistory> b)
    {
        b.ToTable("MachineStateHistory");
        b.Property(x => x.State).HasConversion<byte>();
        b.Property(x => x.StartAt).HasPrecision(3);
        b.Property(x => x.EndAt).HasPrecision(3);
        b.HasOne<Machine>().WithMany().HasForeignKey(x => x.MachineId).OnDelete(DeleteBehavior.Restrict);
        // Exactly one open segment per machine, guarded by the database even if the code has a bug.
        b.HasIndex(x => x.MachineId).IsUnique().HasFilter("[EndAt] IS NULL").HasDatabaseName("UX_MachineStateHistory_OpenPerMachine");
        b.HasIndex(x => new { x.MachineId, x.StartAt });
    }
}

public class MachineTagConfiguration : IEntityTypeConfiguration<MachineTag>
{
    public void Configure(EntityTypeBuilder<MachineTag> b)
    {
        b.ToTable("MachineTags");
        b.Property(x => x.TagName).HasMaxLength(MachineTag.TagNameMaxLength).IsUnicode(false);
        b.Property(x => x.Unit).HasMaxLength(MachineTag.UnitMaxLength);   // nvarchar: "°C", "µm"
        b.Property(x => x.CreatedBy).HasMaxLength(100);
        b.HasOne<Machine>().WithMany().HasForeignKey(x => x.MachineId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.MachineId, x.TagName }).IsUnique();
        b.ToTable(t => t.HasCheckConstraint("CK_MachineTags_Plausible", "[LowerPlausible] < [UpperPlausible]"));
    }
}

public class ProcessValueConfiguration : IEntityTypeConfiguration<ProcessValue>
{
    public void Configure(EntityTypeBuilder<ProcessValue> b)
    {
        b.ToTable("ProcessValues");
        b.Property(x => x.Quality).HasConversion<byte>();
        b.Property(x => x.SourceTs).HasPrecision(3);
        b.Property(x => x.ReceivedAt).HasPrecision(3);
        b.HasIndex(x => x.MsgId).IsUnique();
        // Only this one: the chart / SPC read "a tag over time". A wider index slows every insert (spec 2.3b).
        b.HasIndex(x => new { x.MachineTagId, x.SourceTs });
    }
}

public class AndonCallConfiguration : IEntityTypeConfiguration<AndonCall>
{
    public void Configure(EntityTypeBuilder<AndonCall> b)
    {
        b.ToTable("AndonCalls");
        b.Property(x => x.Type).HasConversion<byte>();
        b.Property(x => x.Status).HasConversion<byte>();
        b.Property(x => x.RaisedBy).HasMaxLength(50).IsUnicode(false);
        b.Property(x => x.AckBy).HasMaxLength(50).IsUnicode(false);
        b.Property(x => x.ResolvedBy).HasMaxLength(50).IsUnicode(false);
        b.Property(x => x.ResolutionNote).HasMaxLength(AndonCall.NoteMaxLength);
        b.Property(x => x.RowVersion).IsRowVersion();   // two supervisors acknowledging at once: the second gets 409
        b.HasOne<Machine>().WithMany().HasForeignKey(x => x.MachineId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<DowntimeReason>().WithMany().HasForeignKey(x => x.DowntimeReasonId).OnDelete(DeleteBehavior.Restrict);
        // At most one open call (Raised = 0, Acknowledged = 1) per machine and type: the auto-raise and an operator's
        // button racing each other cannot open two calls for one problem.
        b.HasIndex(x => new { x.MachineId, x.Type }).IsUnique().HasFilter("[Status] <= 1").HasDatabaseName("UX_AndonCalls_OpenPerMachineType");
        b.HasIndex(x => new { x.Status, x.RaisedAt });
    }
}

public class AndonEscalationRuleConfiguration : IEntityTypeConfiguration<AndonEscalationRule>
{
    public void Configure(EntityTypeBuilder<AndonEscalationRule> b)
    {
        b.ToTable("AndonEscalationRules");
        b.HasKey(x => new { x.AndonType, x.Level });
        b.Property(x => x.AndonType).HasConversion<byte>();
        b.Property(x => x.NotifyRoles).HasMaxLength(AndonEscalationRule.NotifyRolesMaxLength).IsUnicode(false);
        b.Property(x => x.Note).HasMaxLength(AndonEscalationRule.NoteMaxLength);
        b.Property(x => x.CreatedBy).HasMaxLength(100);
        b.Ignore(x => x.Roles);
    }
}
