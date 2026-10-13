using MiniMes.Domain.MasterData.Boms;

namespace MiniMes.UnitTests.MasterData;

// Graph: parent itemId -> component itemIds (one edge per BOM line).
public class BomCycleDetectorTests
{
    private static IReadOnlyList<int>? Find(params (int Parent, int[] Children)[] edges)
        => BomCycleDetector.FindCycle(edges.ToDictionary(e => e.Parent, e => e.Children.ToList()));

    [Fact]
    public void Direct_cycle_A_uses_A()
        => Assert.Equal([1, 1], Find((1, [1])));

    [Fact]
    public void Indirect_cycle_over_three_levels_reports_the_path()
        => Assert.Equal([1, 2, 3, 1], Find((1, [2]), (2, [3]), (3, [1])));

    [Fact]
    public void Path_starts_at_the_repeated_node_not_at_the_root()
        => Assert.Equal([2, 3, 2], Find((1, [2]), (2, [3]), (3, [2])));   // 1 is on the way, not in the loop

    [Fact]
    public void Cycle_not_reachable_from_the_first_root_is_still_found()
        => Assert.Equal([5, 6, 5], Find((1, [2]), (2, []), (5, [6]), (6, [5])));

    [Fact]
    public void Valid_multilevel_bom_is_not_a_cycle()
        => Assert.Null(Find((1, [2, 4]), (2, [3]), (3, [])));            // FG -> semi -> semi -> raw

    // PCB-A uses PCBA-SMT (which uses CAP) and CAP directly: CAP is reached twice, but there is no loop.
    // This is the case a plain "visited" set gets wrong.
    [Fact]
    public void Diamond_is_not_a_cycle()
        => Assert.Null(Find((1, [2, 3]), (2, [3]), (3, [])));

    [Fact]
    public void Diamond_reached_from_two_separate_roots_is_not_a_cycle()
        => Assert.Null(Find((10, [3]), (1, [2, 3]), (2, [3])));

    [Fact]
    public void Empty_graph_has_no_cycle()
        => Assert.Null(Find());

    [Fact]
    public void Component_without_its_own_bom_is_a_leaf()
        => Assert.Null(Find((1, [7, 8])));                               // 7, 8 are not keys: raw materials

    [Fact]
    public void Pcb_a_seed_structure_has_no_cycle()
    {
        // PCB-A(1) -> PCBA-SMT(2), CONN(9), SHIELD(10), BAG(11); PCBA-SMT -> PCB-BARE, RES, CAP, IC, SOLDER-PASTE
        Assert.Null(Find((1, [2, 9, 10, 11]), (2, [3, 4, 5, 6, 7])));
    }
}
