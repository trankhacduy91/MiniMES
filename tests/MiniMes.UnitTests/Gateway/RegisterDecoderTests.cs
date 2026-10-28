using MiniMes.EdgeGateway.Drivers.Modbus;

namespace MiniMes.UnitTests.Gateway;

// Spec M4b 2.4 table: the same number in the four word orders, the classic wrong read, and a float.
public class RegisterDecoderTests
{
    [Theory]
    [InlineData((ushort)0x0001, (ushort)0x86A0, WordOrder.ABCD, 100_000u)]
    [InlineData((ushort)0x86A0, (ushort)0x0001, WordOrder.CDAB, 100_000u)]
    [InlineData((ushort)0x86A0, (ushort)0x0001, WordOrder.ABCD, 2_258_632_705u)]   // CDAB data read as ABCD: "2 billion boards"
    [InlineData((ushort)0x0100, (ushort)0xA086, WordOrder.BADC, 100_000u)]
    [InlineData((ushort)0xA086, (ushort)0x0100, WordOrder.DCBA, 100_000u)]
    [InlineData((ushort)0xFFFF, (ushort)0xFFFF, WordOrder.ABCD, uint.MaxValue)]
    [InlineData((ushort)0x0000, (ushort)0x0000, WordOrder.CDAB, 0u)]
    public void UInt32_in_each_word_order(ushort r0, ushort r1, WordOrder order, uint expected)
    {
        Assert.Equal(expected, RegisterDecoder.ToUInt32(r0, r1, order));
    }

    [Theory]
    [InlineData((ushort)0xFFFF, (ushort)0xFFFE, WordOrder.ABCD, -2)]
    [InlineData((ushort)0xFFFE, (ushort)0xFFFF, WordOrder.CDAB, -2)]
    [InlineData((ushort)0x0001, (ushort)0x86A0, WordOrder.ABCD, 100_000)]
    public void Int32_is_signed(ushort r0, ushort r1, WordOrder order, int expected)
    {
        Assert.Equal(expected, RegisterDecoder.ToInt32(r0, r1, order));
    }

    [Theory]
    [InlineData((ushort)0x42F6, (ushort)0xE979, WordOrder.ABCD)]
    [InlineData((ushort)0xE979, (ushort)0x42F6, WordOrder.CDAB)]
    [InlineData((ushort)0xF642, (ushort)0x79E9, WordOrder.BADC)]
    [InlineData((ushort)0x79E9, (ushort)0xF642, WordOrder.DCBA)]
    public void Float_in_each_word_order(ushort r0, ushort r1, WordOrder order)
    {
        Assert.Equal(123.456f, RegisterDecoder.ToFloat(r0, r1, order), 0.0001f);
    }

    [Fact]
    public void Float_in_the_wrong_order_is_nonsense()
    {
        var wrong = RegisterDecoder.ToFloat(0x42F6, 0xE979, WordOrder.CDAB);

        Assert.True(Math.Abs(wrong) > 1e20f);
    }

    // The glue dispenser's block (simulator): HR0-1 counter CDAB, HR2 state, HR3 alarm.
    [Fact]
    public void Decode_picks_each_tag_out_of_the_block()
    {
        ushort[] block = [0x86A0, 0x0001, 3, 301];

        Assert.Equal(100_000u, RegisterDecoder.Decode(block, 0, RegisterTypes.UInt32, WordOrder.CDAB));
        Assert.Equal((ushort)3, RegisterDecoder.Decode(block, 2, RegisterTypes.UInt16, WordOrder.ABCD));
        Assert.Equal((ushort)301, RegisterDecoder.Decode(block, 3, RegisterTypes.UInt16, WordOrder.ABCD));
    }

    [Fact]
    public void Off_by_one_address_reads_the_neighbour()
    {
        ushort[] block = [0x86A0, 0x0001, 1, 0];

        // "40001" typed as wire address 1 instead of 0: still a plausible-looking number.
        Assert.Equal(65_537u, RegisterDecoder.Decode(block, 1, RegisterTypes.UInt32, WordOrder.CDAB));
    }

    // Regression: a switch over ushort/short/uint/int/float arms used to box every value as float.
    [Fact]
    public void A_large_counter_keeps_every_digit_and_its_type()
    {
        ushort[] block = [0x0001, 0x0100];   // CDAB: 0x0100_0001 = 16,777,217 = 2^24 + 1, not representable as float

        var value = RegisterDecoder.Decode(block, 0, RegisterTypes.UInt32, WordOrder.CDAB);

        Assert.IsType<uint>(value);
        Assert.Equal(16_777_217u, value);
    }

    [Fact]
    public void Int16_is_signed()
    {
        Assert.Equal((short)-1, RegisterDecoder.Decode([0xFFFF], 0, RegisterTypes.Int16, WordOrder.ABCD));
    }

    [Theory]
    [InlineData(RegisterTypes.UInt16, 1)]
    [InlineData(RegisterTypes.Int16, 1)]
    [InlineData(RegisterTypes.UInt32, 2)]
    [InlineData(RegisterTypes.Int32, 2)]
    [InlineData(RegisterTypes.Float32, 2)]
    public void Width_in_registers(string type, int width)
    {
        Assert.Equal(width, RegisterTypes.Width(type));
    }
}
