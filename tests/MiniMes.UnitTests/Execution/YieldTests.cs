using MiniMes.Domain.Execution;

namespace MiniMes.UnitTests.Execution;

// Spec M3 1.2: the worked examples of FPY, final yield and RTY.
public class YieldTests
{
    // 200 boards enter Test, 184 pass at once; 16 NG -> 12 repaired and pass, 4 scrapped.
    [Fact]
    public void Spec_example_fpy_92_final_yield_98()
    {
        Assert.Equal(92.00m, Yield.Percent(184, 200));
        Assert.Equal(98.00m, Yield.FinalYield(184 + 12, 4));
    }

    // The trap of 1.2: counting scans (196 good / 212 scans) instead of serials looks close but is another number.
    [Fact]
    public void Counting_scans_instead_of_first_passes_gives_a_different_number()
        => Assert.NotEqual(Yield.Percent(184, 200), Yield.Percent(196, 196 + 16));

    // SMT 98 %, Assembly 99 %, Test 92 %, Pack 99.5 % -> RTY 88.8 %.
    [Fact]
    public void Spec_example_rty_is_the_product_of_the_steps()
        => Assert.Equal(88.81m, Yield.Rolled([(980, 1000), (990, 1000), (920, 1000), (995, 1000)]));

    [Fact]
    public void Nothing_to_divide_by_is_null_not_zero()
    {
        Assert.Null(Yield.Percent(0, 0));
        Assert.Null(Yield.FinalYield(0, 0));        // all boards still in process
        Assert.Null(Yield.Rolled([(0, 0), (0, 0)]));
    }

    // A step no board has reached yet does not make the line's RTY 0.
    [Fact]
    public void Steps_without_boards_are_left_out_of_rty()
        => Assert.Equal(70.00m, Yield.Rolled([(10, 10), (7, 10), (0, 0)]));
}
