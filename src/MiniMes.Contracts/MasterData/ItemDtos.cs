namespace MiniMes.Contracts.MasterData;

public sealed record ItemDto(
    int Id,
    string Code,
    string Name,
    string Type,          // Raw | Semi | Finished
    string Uom,
    bool IsSerialized,
    bool IsLotControlled,
    bool SkipIqc,
    bool IsActive);

public sealed record CreateItemRequest(
    string Code,
    string Name,
    string Type,
    string Uom,
    bool IsSerialized,
    bool IsLotControlled,
    bool SkipIqc = false);

// Code, Type, Uom and traceability flags cannot change after creation (BOMs, lots and serials rely on them).
public sealed record UpdateItemRequest(string Name, bool SkipIqc);

// Query string of GET /api/items.
public sealed class ItemFilter
{
    public const int MaxPageSize = 200;

    public string? Search { get; init; }    // contains, on Code or Name
    public string? Type { get; init; }
    public bool? IsActive { get; init; }
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 50;
}
