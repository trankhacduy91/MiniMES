using MiniMes.Contracts.MasterData;
using MiniMes.Domain.MasterData.Items;

namespace MiniMes.Application.MasterData.Items;

// Entities never leave the Application layer: callers get DTOs from MiniMes.Contracts.
internal static class ItemMapping
{
    public static ItemDto ToDto(this Item i)
        => new(i.Id, i.Code, i.Name, i.Type.ToString(), i.Uom, i.IsSerialized, i.IsLotControlled, i.SkipIqc, i.IsActive);
}
