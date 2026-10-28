namespace MiniMes.EdgeGateway.Drivers.Modbus;

// Spec M4b 2.4: a Modbus register is 16 bits; a 32-bit number sits in two consecutive registers r0, r1, and the
// machine's documentation rarely says in which order. A, B, C, D = the bytes of the number, most significant first.
//   ABCD  r0 = AB, r1 = CD   big-endian, "the standard"
//   CDAB  r0 = CD, r1 = AB   words swapped (very common)
//   BADC  r0 = BA, r1 = DC   bytes swapped inside each word
//   DCBA  r0 = DC, r1 = BA   little-endian
// Every mistake here still returns a number (100,000 read ABCD instead of CDAB = 2,258,632,705): no exception warns
// you, so this file is pure and covered by a table of known values.
public enum WordOrder { ABCD, CDAB, BADC, DCBA }

public static class RegisterTypes
{
    public const string UInt16 = "uint16";
    public const string Int16 = "int16";
    public const string UInt32 = "uint32";
    public const string Int32 = "int32";
    public const string Float32 = "float32";
    public static readonly string[] All = [UInt16, Int16, UInt32, Int32, Float32];

    public static int Width(string type) => type is UInt16 or Int16 ? 1 : 2;
}

public static class RegisterDecoder
{
    private static ushort Swap(ushort v) => (ushort)((v << 8) | (v >> 8));

    public static uint ToUInt32(ushort r0, ushort r1, WordOrder order) => order switch
    {
        WordOrder.ABCD => ((uint)r0 << 16) | r1,
        WordOrder.CDAB => ((uint)r1 << 16) | r0,
        WordOrder.BADC => ((uint)Swap(r0) << 16) | Swap(r1),
        WordOrder.DCBA => ((uint)Swap(r1) << 16) | Swap(r0),
        _ => throw new ArgumentOutOfRangeException(nameof(order)),
    };

    public static int ToInt32(ushort r0, ushort r1, WordOrder order) => unchecked((int)ToUInt32(r0, r1, order));

    public static float ToFloat(ushort r0, ushort r1, WordOrder order) => BitConverter.Int32BitsToSingle(ToInt32(r0, r1, order));

    // One tag out of the block read in one request: offset is relative to the block's start address.
    // The value keeps the device's type (ushort, short, uint, int, float); the normalizer (step 4) converts it.
    // Every arm is cast to object on purpose: without it the switch takes the arms' best common type, float, and a
    // uint32 counter above 16,777,216 silently loses its last digits (float has a 24-bit mantissa).
    public static object Decode(IReadOnlyList<ushort> block, int offset, string type, WordOrder order) => type switch
    {
        RegisterTypes.UInt16 => (object)block[offset],
        RegisterTypes.Int16 => (object)unchecked((short)block[offset]),
        RegisterTypes.UInt32 => (object)ToUInt32(block[offset], block[offset + 1], order),
        RegisterTypes.Int32 => (object)ToInt32(block[offset], block[offset + 1], order),
        RegisterTypes.Float32 => (object)ToFloat(block[offset], block[offset + 1], order),
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "unknown register type"),
    };
}
