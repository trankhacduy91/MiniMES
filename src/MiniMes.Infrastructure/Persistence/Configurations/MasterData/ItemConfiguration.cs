using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MiniMes.Domain.MasterData;
using MiniMes.Domain.MasterData.Items;

namespace MiniMes.Infrastructure.Persistence.Configurations.MasterData;

public class ItemConfiguration : IEntityTypeConfiguration<Item>
{
    public void Configure(EntityTypeBuilder<Item> b)
    {
        b.ToTable("Items");
        b.Property(x => x.Code).HasMaxLength(MasterDataRules.CodeMaxLength);
        // Default SQL Server collation is case-insensitive: "res-10k" and "RES-10K" collide, as rule 1 wants.
        b.HasIndex(x => x.Code).IsUnique();
        b.Property(x => x.Name).HasMaxLength(MasterDataRules.NameMaxLength);
        b.Property(x => x.Type).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Uom).HasMaxLength(10);
        b.Property(x => x.CreatedBy).HasMaxLength(100);
    }
}
