using MiniMes.Domain.Common;
using MiniMes.Domain.MasterData.Boms;
using MiniMes.Domain.MasterData.Items;

namespace MiniMes.UnitTests.MasterData;

// The PCB-A example of spec M1 1.2.4, computed without a database.
public class BomExplosionTests
{
    private const int PcbA = 1, PcbaSmt = 2, PcbBare = 3, Res = 4, Cap = 5, Ic = 6, Solder = 7, Conn = 8, Shield = 9, Bag = 10;

    private static readonly Dictionary<int, IReadOnlyList<BomLine>> PcbABoms = new()
    {
        [PcbA] = Lines(PcbA, (PcbaSmt, 1m, 0.01m, 20), (Conn, 1m, 0.005m, 20), (Shield, 1m, 0.005m, 20), (Bag, 1m, 0m, 40)),
        [PcbaSmt] = Lines(PcbaSmt, (PcbBare, 1m, 0.005m, 10), (Res, 12m, 0.02m, 10), (Cap, 20m, 0.02m, 10),
            (Ic, 1m, 0.005m, 10), (Solder, 0.0006m, 0.05m, 10)),
    };

    private static IReadOnlyList<BomLine> Lines(int parent, params (int Id, decimal QtyPer, decimal Scrap, int Seq)[] lines)
    {
        var bom = Bom.CreateDraft(parent, 1);
        foreach (var l in lines) bom.AddLine(l.Id, l.QtyPer, l.Scrap, l.Seq);
        return bom.Lines;
    }

    private static IReadOnlyDictionary<int, decimal> Explode(Dictionary<int, IReadOnlyList<BomLine>> boms, int root, decimal qty)
        => BomExplosion.Totals(BomExplosion.BuildTree(root, qty, id => boms.GetValueOrDefault(id)));

    [Theory]
    [InlineData(PcbaSmt, "202", 202)]
    [InlineData(PcbBare, "203.01", 204)]
    [InlineData(Res, "2472.48", 2473)]
    [InlineData(Cap, "4120.8", 4121)]
    [InlineData(Ic, "203.01", 204)]
    [InlineData(Conn, "201", 201)]
    [InlineData(Shield, "201", 201)]
    [InlineData(Bag, "200", 200)]
    public void Pcb_a_times_200_matches_the_spec_table(int itemId, string exact, int rounded)
    {
        var totals = Explode(PcbABoms, PcbA, 200);

        Assert.Equal(decimal.Parse(exact, System.Globalization.CultureInfo.InvariantCulture), totals[itemId]);
        Assert.Equal(rounded, Units.RoundRequirement(Units.Pieces, totals[itemId]));
    }

    [Fact]
    public void Solder_paste_keeps_its_exact_kg_value()
    {
        var kg = Explode(PcbABoms, PcbA, 200)[Solder];

        Assert.Equal(0.12726m, Units.RoundRequirement(Units.Kilogram, kg));
        Assert.Equal(0.127m, Math.Round(kg, 3)); // what the spec table shows
    }

    // The trap of spec 1.2.4: forgetting the 1% scrap of PCBA-SMT gives 2448 resistors, 25 short.
    [Fact]
    public void Scrap_of_the_middle_level_carries_down()
        => Assert.NotEqual(2448m, Math.Ceiling(Explode(PcbABoms, PcbA, 200)[Res]));

    // Diamond: CAP used inside PCBA-SMT (20) and directly by PCB-A (2). Branches are added, then rounded once.
    [Fact]
    public void Same_component_in_two_branches_is_added()
    {
        var boms = new Dictionary<int, IReadOnlyList<BomLine>>
        {
            [PcbA] = Lines(PcbA, (PcbaSmt, 1m, 0.01m, 20), (Cap, 2m, 0.02m, 20)),
            [PcbaSmt] = Lines(PcbaSmt, (Cap, 20m, 0.02m, 10)),
        };

        Assert.Equal(4120.8m + 408m, Explode(boms, PcbA, 200)[Cap]);
    }

    [Fact]
    public void Tree_keeps_the_structure_for_the_ui()
    {
        var tree = BomExplosion.BuildTree(PcbA, 200, id => PcbABoms.GetValueOrDefault(id));

        Assert.Equal(200m, tree.RequiredQty);
        Assert.Equal([PcbaSmt, Conn, Shield, Bag], tree.Children.Select(c => c.ItemId)); // by OperationSeq
        Assert.Equal(5, tree.Children[0].Children.Count);
        Assert.Empty(tree.Children[1].Children);                                       // Raw: leaf
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Quantity_must_be_positive(decimal qty)
        => Assert.Throws<DomainException>(() => BomExplosion.BuildTree(PcbA, qty, id => PcbABoms.GetValueOrDefault(id)));

    // A cycle that slipped into the data (e.g. edited by hand in SSMS) must stop, not recurse forever.
    [Fact]
    public void Cycle_in_the_data_stops_at_max_depth()
    {
        var boms = new Dictionary<int, IReadOnlyList<BomLine>>
        {
            [1] = Lines(1, (2, 1m, 0m, 10)),
            [2] = Lines(2, (1, 1m, 0m, 10)),
        };

        Assert.Throws<DomainException>(() => BomExplosion.BuildTree(1, 1, id => boms.GetValueOrDefault(id)));
    }
}
