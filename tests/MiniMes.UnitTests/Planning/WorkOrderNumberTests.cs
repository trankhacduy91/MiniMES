using MiniMes.Domain.Common;
using MiniMes.Domain.Planning;

namespace MiniMes.UnitTests.Planning;

public class WorkOrderNumberTests
{
    [Theory]
    [InlineData(1, "WO-2610-00001")]
    [InlineData(12345, "WO-2610-12345")]
    [InlineData(100000, "WO-2610-100000")]   // the global sequence never resets: a 6th digit, still unique
    public void Formats_plant_month_and_sequence(long sequence, string expected)
        => Assert.Equal(expected, WorkOrderNumber.Format(new DateOnly(2026, 10, 31), sequence));

    [Fact]
    public void Sequence_below_1_is_refused()
        => Assert.Throws<DomainException>(() => WorkOrderNumber.Format(new DateOnly(2026, 10, 1), 0));
}
