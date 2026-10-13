using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MiniMes.Domain.MasterData.Boms;
using MiniMes.Domain.MasterData.Items;

namespace MiniMes.Infrastructure.Persistence.Configurations.MasterData;

public class BomConfiguration : IEntityTypeConfiguration<Bom>
{
    public void Configure(EntityTypeBuilder<Bom> b)
    {
        b.ToTable("Boms");
        b.HasIndex(x => new { x.ItemId, x.Version }).IsUnique();
        // Version rules enforced by the database too, so two racing requests cannot break them (-> 409):
        // at most one Draft per item, and at most one Approved version still open (EffectiveTo NULL).
        b.HasIndex(x => x.ItemId, "UX_Boms_OneDraftPerItem").IsUnique().HasFilter("[Status] = 'Draft'");
        b.HasIndex(x => x.ItemId, "UX_Boms_OneOpenApprovedPerItem").IsUnique()
            .HasFilter("[Status] = 'Approved' AND [EffectiveTo] IS NULL");
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.CreatedBy).HasMaxLength(100);

        // Item -> Bom and Item -> BomLine are two paths to the same table: SQL Server refuses multiple
        // cascade paths, and deleting an item must never delete its BOM history anyway.
        b.HasOne<Item>().WithMany().HasForeignKey(x => x.ItemId).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(x => x.Lines).WithOne().HasForeignKey(l => l.BomId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class BomLineConfiguration : IEntityTypeConfiguration<BomLine>
{
    public void Configure(EntityTypeBuilder<BomLine> b)
    {
        b.ToTable("BomLines", t =>
        {
            // Last line of defence behind the Domain checks.
            t.HasCheckConstraint("CK_BomLines_ScrapRate", "[ScrapRate] >= 0 AND [ScrapRate] < 1");
            t.HasCheckConstraint("CK_BomLines_QtyPer", "[QtyPer] > 0");
        });
        // Without HasPrecision EF uses decimal(18,2): ScrapRate 0.005 would silently become 0.01.
        b.Property(x => x.QtyPer).HasPrecision(18, 6);
        b.Property(x => x.ScrapRate).HasPrecision(9, 6);
        b.HasIndex(x => new { x.BomId, x.ComponentItemId }).IsUnique();
        b.HasOne<Item>().WithMany().HasForeignKey(x => x.ComponentItemId).OnDelete(DeleteBehavior.Restrict);
    }
}
