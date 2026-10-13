using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MiniMes.Domain.MasterData;
using MiniMes.Domain.MasterData.Equipment;
using MiniMes.Domain.MasterData.Items;
using MiniMes.Domain.MasterData.Routings;

namespace MiniMes.Infrastructure.Persistence.Configurations.MasterData;

public class RoutingConfiguration : IEntityTypeConfiguration<Routing>
{
    public void Configure(EntityTypeBuilder<Routing> b)
    {
        b.ToTable("Routings");
        b.HasIndex(x => new { x.ItemId, x.Version }).IsUnique();
        // Version rules enforced by the database too, so two racing requests cannot break them (-> 409):
        // at most one Draft per item, and at most one Approved version still open (EffectiveTo NULL).
        b.HasIndex(x => x.ItemId, "UX_Routings_OneDraftPerItem").IsUnique().HasFilter("[Status] = 'Draft'");
        b.HasIndex(x => x.ItemId, "UX_Routings_OneOpenApprovedPerItem").IsUnique()
            .HasFilter("[Status] = 'Approved' AND [EffectiveTo] IS NULL");
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.CreatedBy).HasMaxLength(100);
        b.HasOne<Item>().WithMany().HasForeignKey(x => x.ItemId).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(x => x.Steps).WithOne().HasForeignKey(s => s.RoutingId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class RoutingStepConfiguration : IEntityTypeConfiguration<RoutingStep>
{
    public void Configure(EntityTypeBuilder<RoutingStep> b)
    {
        b.ToTable("RoutingSteps", t => t.HasCheckConstraint("CK_RoutingSteps_IdealCycleTime", "[IdealCycleTimeSec] > 0"));
        b.HasIndex(x => new { x.RoutingId, x.Seq }).IsUnique();
        b.Property(x => x.OperationCode).HasMaxLength(MasterDataRules.CodeMaxLength);
        b.Property(x => x.IdealCycleTimeSec).HasPrecision(9, 3); // some machines run < 1 s per unit
        b.Property(x => x.SopUrl).HasMaxLength(RoutingStep.SopUrlMaxLength);
        b.HasOne<WorkCenter>().WithMany().HasForeignKey(x => x.WorkCenterId).OnDelete(DeleteBehavior.Restrict);
    }
}
