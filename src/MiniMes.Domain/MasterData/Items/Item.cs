using MiniMes.Domain.Common;

namespace MiniMes.Domain.MasterData.Items;

// A part number: everything the plant buys, makes or sells.
public class Item : AuditableEntity, IAudited
{
    public int Id { get; private set; }
    public string Code { get; private set; }
    public string Name { get; private set; }
    public ItemType Type { get; private set; }
    public string Uom { get; private set; }
    public bool IsSerialized { get; private set; }     // one serial per unit (finished goods)
    public bool IsLotControlled { get; private set; }  // traced by lot/reel (raw material)
    public bool SkipIqc { get; private set; }          // [v2] exempt from incoming inspection (M6), Raw only
    public bool IsActive { get; private set; } = true;

    private Item() { Code = Name = Uom = string.Empty; }

    public static Item Create(string code, string name, ItemType type, string uom,
        bool isSerialized, bool isLotControlled, bool skipIqc = false)
    {
        MasterDataRules.RequireCode(code, "Item");
        MasterDataRules.RequireName(name);
        RequireUom(uom);
        RequireTraceability(type, isSerialized, isLotControlled, skipIqc);

        return new Item
        {
            Code = code,
            Name = name,
            Type = type,
            Uom = uom,
            IsSerialized = isSerialized,
            IsLotControlled = isLotControlled,
            SkipIqc = skipIqc,
        };
    }

    public void SetActive(bool isActive) => IsActive = isActive;

    private static void RequireUom(string uom)
    {
        if (!Units.IsValid(uom))
            throw new DomainException($"Unit '{uom}' is not supported. Use one of: {string.Join(", ", Units.All)}.");
    }

    // How each type is traced (spec M1 1.2.1): raw material by lot, finished goods by serial,
    // semi-finished follows the finished good's serial (assigned at WO release) so has neither.
    private static void RequireTraceability(ItemType type, bool isSerialized, bool isLotControlled, bool skipIqc)
    {
        var (serial, lot) = type switch
        {
            ItemType.Raw => (false, true),
            ItemType.Finished => (true, false),
            _ => (false, false),
        };
        if (isSerialized != serial || isLotControlled != lot)
            throw new DomainException(
                $"{type} items must have IsSerialized={serial} and IsLotControlled={lot}.");
        if (skipIqc && type != ItemType.Raw)
            throw new DomainException("SkipIqc is only valid for Raw items (IQC inspects purchased material).");
    }
}
