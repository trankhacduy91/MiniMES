using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MiniMes.Domain.MasterData;
using MiniMes.Domain.MasterData.DefectCodes;
using MiniMes.Domain.MasterData.Downtime;

namespace MiniMes.Infrastructure.Persistence.Configurations.MasterData;

public class DefectCodeConfiguration : IEntityTypeConfiguration<DefectCode>
{
    public void Configure(EntityTypeBuilder<DefectCode> b)
    {
        b.ToTable("DefectCodes");
        b.Property(x => x.Code).HasMaxLength(MasterDataRules.CodeMaxLength);
        b.HasIndex(x => x.Code).IsUnique();
        b.Property(x => x.Name).HasMaxLength(MasterDataRules.NameMaxLength);
        b.Property(x => x.Category).HasMaxLength(DefectCode.CategoryMaxLength);
        b.Property(x => x.CreatedBy).HasMaxLength(100);
    }
}

public class DowntimeReasonConfiguration : IEntityTypeConfiguration<DowntimeReason>
{
    public void Configure(EntityTypeBuilder<DowntimeReason> b)
    {
        b.ToTable("DowntimeReasons", t => t.HasCheckConstraint("CK_DowntimeReasons_UnplannedCounts",
            "[Category] <> 'Unplanned' OR [CountsAgainstAvailability] = 1"));
        b.Property(x => x.Code).HasMaxLength(MasterDataRules.CodeMaxLength);
        b.HasIndex(x => x.Code).IsUnique();
        b.Property(x => x.Name).HasMaxLength(MasterDataRules.NameMaxLength);
        b.Property(x => x.Category).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.CreatedBy).HasMaxLength(100);
    }
}
