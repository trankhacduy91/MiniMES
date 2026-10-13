using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MiniMes.Domain.Factory;
using MiniMes.Domain.MasterData;
using MiniMes.Domain.MasterData.Equipment;

namespace MiniMes.Infrastructure.Persistence.Configurations.MasterData;

public class WorkCenterConfiguration : IEntityTypeConfiguration<WorkCenter>
{
    public void Configure(EntityTypeBuilder<WorkCenter> b)
    {
        b.ToTable("WorkCenters");
        b.Property(x => x.Code).HasMaxLength(MasterDataRules.CodeMaxLength);
        b.HasIndex(x => x.Code).IsUnique();
        b.Property(x => x.Name).HasMaxLength(MasterDataRules.NameMaxLength);
        b.Property(x => x.Type).HasMaxLength(MasterDataRules.CodeMaxLength);
        b.Property(x => x.CreatedBy).HasMaxLength(100);
        b.HasOne<Line>().WithMany().HasForeignKey(x => x.LineId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class MachineConfiguration : IEntityTypeConfiguration<Machine>
{
    public void Configure(EntityTypeBuilder<Machine> b)
    {
        b.ToTable("Machines");
        b.Property(x => x.Code).HasMaxLength(MasterDataRules.CodeMaxLength);
        b.HasIndex(x => x.Code).IsUnique();
        b.Property(x => x.Name).HasMaxLength(MasterDataRules.NameMaxLength);
        b.Property(x => x.Type).HasMaxLength(50);
        // Required + unique: two machines on one topic would mix their counters in OEE (M4/M5).
        b.Property(x => x.MqttTopicKey).HasMaxLength(MasterDataRules.CodeMaxLength);
        b.HasIndex(x => x.MqttTopicKey).IsUnique();
        b.Property(x => x.DataSource).HasConversion<string>().HasMaxLength(10);
        b.Property(x => x.CreatedBy).HasMaxLength(100);
        b.HasOne<WorkCenter>().WithMany().HasForeignKey(x => x.WorkCenterId).OnDelete(DeleteBehavior.Restrict);
    }
}
