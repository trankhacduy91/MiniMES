namespace MiniMes.Domain.MasterData.Items;

// One base unit per item: the unit BOMs and machines consume in. Reels/boxes are lot quantities (M6),
// not units, so there is no conversion table in the MVP.
public static class Units
{
    public const string Pieces = "pcs";
    public const string Kilogram = "kg";
    public const string Gram = "g";
    public const string Meter = "m";

    public static readonly IReadOnlyList<string> All = [Pieces, Kilogram, Gram, Meter];

    public static bool IsValid(string uom) => All.Contains(uom);

    // Discrete units are counted, so requirements round UP to whole numbers (only at the end of an explosion).
    public static bool IsDiscrete(string uom) => uom == Pieces;
}
