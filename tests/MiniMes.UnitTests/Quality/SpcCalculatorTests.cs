using ClosedXML.Excel;
using MiniMes.Domain.Quality;
using MiniMes.Domain.Quality.Spc;

namespace MiniMes.UnitTests.Quality;

// Spec M7 step 3. Expected values come from the hand calculation (spec 1.2, docs/quality-erd.md section 1), never
// from running this code. double needs a precision on every Assert.Equal (2.1 trap 1).
public class SpcCalculatorTests
{
    // Spec 1.2: the 3V3 rail on 10 boards.
    private static readonly double[] Data = [3.32, 3.36, 3.28, 3.33, 3.31, 3.37, 3.27, 3.32, 3.35, 3.29];

    [Fact]
    public void Compute_matches_the_hand_calculation()
    {
        var s = SpcCalculator.Compute(Data);

        Assert.Equal(10, s.Count);
        Assert.Equal(3.32, s.Mean, precision: 6);
        Assert.Equal(0.03367, s.Sigma, precision: 5);      // √(0.0102 / 9)
        Assert.Equal(3.421, s.Ucl, precision: 3);
        Assert.Equal(3.219, s.Lcl, precision: 3);
    }

    // Bessel: dividing by n would give 0.03194 (population σ), ~5 % narrower limits.
    [Fact]
    public void Sigma_is_the_sample_sigma_divided_by_n_minus_1()
    {
        var sigma = SpcCalculator.Compute(Data).Sigma;
        Assert.NotEqual(0.03194, sigma, precision: 4);
        Assert.Equal(Math.Sqrt(0.0102 / 9), sigma, precision: 10);
    }

    [Fact]
    public void Capability_matches_the_hand_calculation()
    {
        var c = SpcCalculator.CalcCapability(SpcCalculator.Compute(Data), usl: 3.465, lsl: 3.135);

        Assert.Equal(1.634, c.Cp!.Value, precision: 3);
        Assert.Equal(1.436, c.Cpu!.Value, precision: 3);
        Assert.Equal(1.832, c.Cpl!.Value, precision: 3);
        Assert.Equal(1.436, c.Cpk!.Value, precision: 3);   // the nearer limit: mean 3.32 sits 0.02 above target, toward USL
    }

    [Fact]
    public void MovingRange_matches_the_hand_calculation()
    {
        var (mrBar, uclMr) = SpcCalculator.MovingRange(Data);

        Assert.Equal(0.0544, mrBar, precision: 4);          // 0.49 / 9
        Assert.Equal(0.178, uclMr, precision: 3);
    }

    // The moving range follows measuring order: the same values sorted vary much less from point to point.
    [Fact]
    public void MovingRange_depends_on_the_order_of_the_points()
    {
        var sorted = Data.Order().ToArray();
        Assert.Equal(0.1 / 9, SpcCalculator.MovingRange(sorted).MrBar, precision: 6);   // (3.37 − 3.27) / 9
        Assert.Equal(SpcCalculator.Compute(Data).Sigma, SpcCalculator.Compute(sorted).Sigma, precision: 10);
    }

    // Flatness: only USL. No Cp (no width), Cpk is the one side there is.
    [Fact]
    public void Capability_with_only_usl_has_no_cp_and_cpk_is_cpu()
    {
        var c = SpcCalculator.CalcCapability(SpcCalculator.Compute(Data), usl: 3.465, lsl: null);

        Assert.Null(c.Cp);
        Assert.Null(c.Cpl);
        Assert.Equal(1.436, c.Cpu!.Value, precision: 3);
        Assert.Equal(c.Cpu, c.Cpk);
    }

    [Fact]
    public void Capability_with_only_lsl_has_no_cp_and_cpk_is_cpl()
    {
        var c = SpcCalculator.CalcCapability(SpcCalculator.Compute(Data), usl: null, lsl: 3.135);

        Assert.Equal((null, null), (c.Cp, c.Cpu));
        Assert.Equal(1.832, c.Cpk!.Value, precision: 3);
    }

    // σ = 0 (every value equal): no division by zero, capability "cannot be computed". 3.3 three times is the
    // floating-point trap: summed and divided, the mean is 3.2999999999999994 and σ ≈ 5e-16, Cpk ≈ 10^14.
    [Theory]
    [InlineData(3.3, 3)]
    [InlineData(3.31, 7)]
    [InlineData(245.1, 7)]
    public void All_equal_values_give_sigma_zero_and_no_capability(double value, int count)
    {
        var s = SpcCalculator.Compute(Enumerable.Repeat(value, count).ToArray());

        Assert.Equal((value, 0.0, value, value), (s.Mean, s.Sigma, s.Ucl, s.Lcl));
        Assert.Equal(new Capability(null, null, null, null), SpcCalculator.CalcCapability(s, usl: 3.465, lsl: 3.135));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Fewer_than_two_values_cannot_be_computed(int count)
    {
        var values = Enumerable.Repeat(3.3, count).ToArray();
        Assert.Throws<ArgumentException>(() => SpcCalculator.Compute(values));
        Assert.Throws<ArgumentException>(() => SpcCalculator.MovingRange(values));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void A_value_that_is_not_a_number_is_refused(double broken)
        => Assert.Throws<ArgumentException>(() => SpcCalculator.Compute([3.3, broken, 3.31]));

    [Fact]
    public void Capability_refuses_usl_below_lsl()
        => Assert.Throws<ArgumentException>(() => SpcCalculator.CalcCapability(SpcCalculator.Compute(Data), usl: 3.1, lsl: 3.4));

    // Spec check "compare with Excel (STDEV.S)": an independent spreadsheet engine (ClosedXML's formula evaluator)
    // computes the same column, so the expected values do not come from this code.
    [Fact]
    public void Matches_a_spreadsheet_on_the_same_column()
    {
        using var book = new XLWorkbook();
        var sheet = book.AddWorksheet("spc");
        for (var i = 0; i < Data.Length; i++) sheet.Cell(i + 1, 1).Value = Data[i];
        double Excel(string formula) => sheet.Evaluate(formula).GetNumber();

        var s = SpcCalculator.Compute(Data);
        Assert.Equal(Excel("AVERAGE(A1:A10)"), s.Mean, precision: 10);
        Assert.Equal(Excel("STDEV.S(A1:A10)"), s.Sigma, precision: 10);
        Assert.NotEqual(Excel("STDEV.P(A1:A10)"), s.Sigma, precision: 4);   // the population σ is the other one
    }

    // The baseline row (ControlLimitSet, decimal) and this calculator (double) use the same constant.
    [Fact]
    public void D4_is_the_same_for_the_stored_baseline_and_the_calculator()
        => Assert.Equal((double)ControlLimitSet.D4, SpcCalculator.D4);
}
