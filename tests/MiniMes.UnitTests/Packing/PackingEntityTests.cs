using MiniMes.Domain.Common;
using MiniMes.Domain.Packing;

namespace MiniMes.UnitTests.Packing;

// Spec M3b step 1: what the entities refuse on their own. The packing rules (P2-P9) come in step 2.
public class PackingEntityTests
{
    private static readonly DateTime Now = new(2026, 11, 3, 6, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(0, 20)]
    [InlineData(10, 0)]
    [InlineData(PackingSpec.MaxQtyPerCarton + 1, 20)]
    [InlineData(10, PackingSpec.MaxCartonsPerPallet + 1)]
    public void Packing_spec_needs_sensible_counts(int qtyPerCarton, int cartonsPerPallet)
        => Assert.Throws<DomainException>(() => new PackingSpec(1, qtyPerCarton, cartonsPerPallet));

    [Fact]
    public void Packing_spec_can_be_changed()
    {
        var spec = new PackingSpec(1, 10, 20);

        spec.Change(12, 15);

        Assert.Equal((12, 15), (spec.QtyPerCarton, spec.CartonsPerPallet));
        Assert.Throws<DomainException>(() => spec.Change(-1, 15));
        Assert.Equal((12, 15), (spec.QtyPerCarton, spec.CartonsPerPallet));   // a refused change leaves it as it was
    }

    [Fact]
    public void New_carton_and_pallet_are_open_and_empty()
    {
        var carton = new Carton("C261103000001", 7, 3, 5, Now);
        var pallet = new Pallet("P261103000001", 3, 5, Now);

        Assert.Equal((CartonStatus.Open, 0, false, (int?)null, Now), (carton.Status, carton.Qty, carton.IsPartial, carton.PalletId, carton.OpenedAt));
        Assert.Equal((PalletStatus.Open, 0, Now), (pallet.Status, pallet.CartonCount, pallet.OpenedAt));
    }

    [Fact]
    public void Numbers_longer_than_the_column_are_refused()
    {
        Assert.Throws<DomainException>(() => new Carton(new string('C', Carton.NumberMaxLength + 1), 7, 3, 5, Now));
        Assert.Throws<DomainException>(() => new Pallet(new string('P', Pallet.NumberMaxLength + 1), 3, 5, Now));
    }
}
