using MiniMes.Domain.Common;
using MiniMes.Domain.MasterData.Items;

namespace MiniMes.UnitTests.MasterData;

public class ItemTests
{
    // How each type is traced: raw by lot, finished by serial, semi follows the finished serial.
    [Theory]
    [InlineData(ItemType.Raw, false, true)]
    [InlineData(ItemType.Semi, false, false)]
    [InlineData(ItemType.Finished, true, false)]
    public void Valid_traceability_per_type_is_accepted(ItemType type, bool serialized, bool lot)
    {
        var item = Item.Create("X-1", "X", type, Units.Pieces, serialized, lot);

        Assert.Equal(type, item.Type);
        Assert.True(item.IsActive);
    }

    [Theory]
    [InlineData(ItemType.Raw, true, true)]        // spec step 2: Item.Create(Raw, isSerialized: true) must fail
    [InlineData(ItemType.Raw, false, false)]
    [InlineData(ItemType.Finished, false, false)]
    [InlineData(ItemType.Finished, true, true)]
    [InlineData(ItemType.Semi, true, false)]
    public void Wrong_traceability_for_type_is_rejected(ItemType type, bool serialized, bool lot)
        => Assert.Throws<DomainException>(() => Item.Create("X-1", "X", type, Units.Pieces, serialized, lot));

    [Theory]
    [InlineData(ItemType.Semi, false, false)]
    [InlineData(ItemType.Finished, true, false)]
    public void SkipIqc_is_only_for_raw_material(ItemType type, bool serialized, bool lot)
        => Assert.Throws<DomainException>(() => Item.Create("X-1", "X", type, Units.Pieces, serialized, lot, skipIqc: true));

    [Fact]
    public void Raw_material_can_skip_iqc()
        => Assert.True(Item.Create("PACK-BAG", "Bag", ItemType.Raw, Units.Pieces, false, true, skipIqc: true).SkipIqc);

    [Theory]
    [InlineData("xyz")]
    [InlineData("reel")]   // a reel is a lot quantity (M6), not a base unit
    [InlineData("PCS")]
    public void Unknown_unit_is_rejected(string uom)
        => Assert.Throws<DomainException>(() => Item.Create("X-1", "X", ItemType.Raw, uom, false, true));

    [Theory]
    [InlineData("RES-10K-0402")]
    [InlineData("SOLDER_PASTE")]
    [InlineData("CAP.100N")]
    public void Valid_codes_are_accepted(string code)
        => Item.Create(code, "X", ItemType.Raw, Units.Pieces, false, true);

    [Theory]
    [InlineData("res-10k")]     // lower case: two codes differing only by case confuse people
    [InlineData("RES 10K")]
    [InlineData("RES/10K")]
    public void Invalid_codes_are_rejected(string code)
        => Assert.Throws<DomainException>(() => Item.Create(code, "X", ItemType.Raw, Units.Pieces, false, true));
}
