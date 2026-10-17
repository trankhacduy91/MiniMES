using MiniMes.Domain.Common;
using MiniMes.Domain.Planning;

namespace MiniMes.UnitTests.Planning;

// Spec M2 step 4 / DoD: SerialFormat has unit tests (example of spec 2.4, sequence out of range refused).
public class SerialFormatTests
{
    private static readonly DateOnly Oct1 = new(2026, 10, 1);

    [Fact]
    public void Spec_example()
        => Assert.Equal("PCBA2610010001232", SerialFormat.Build("PCB-A", Oct1, 123));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1_000_000)]
    public void Sequence_out_of_range_is_refused(long sequence)
        => Assert.Throws<DomainException>(() => SerialFormat.Build("PCB-A", Oct1, sequence));

    [Theory]
    [InlineData("PCBA-SMT", "PCBA")]   // longer codes keep the first 4 letters/digits
    [InlineData("ab", "ABXX")]         // short codes are padded
    [InlineData("P.C-B_1", "PCB1")]
    public void Model_is_4_upper_case_letters_or_digits(string itemCode, string model)
        => Assert.StartsWith(model + "261001", SerialFormat.Build(itemCode, Oct1, 1));

    [Fact]
    public void Every_serial_has_17_characters_and_a_valid_check_digit()
    {
        foreach (var seq in new long[] { 1, 9, 123, 45_678, SerialFormat.MaxSequence })
        {
            var serial = SerialFormat.Build("PCB-A", Oct1, seq);
            Assert.Equal(SerialFormat.Length, serial.Length);
            Assert.True(SerialFormat.HasValidCheckDigit(serial));
        }
    }

    [Theory]
    [InlineData("PCBA2610010001233")]   // wrong check digit
    [InlineData("PCBA2610010001322")]   // two digits swapped: the 3,1 weights catch it
    [InlineData("PCBA261001000123")]    // too short
    [InlineData("PCBA-610010001232")]   // not a letter or digit
    public void Mistyped_serial_fails_the_check_digit(string serial)
        => Assert.False(SerialFormat.HasValidCheckDigit(serial));
}
