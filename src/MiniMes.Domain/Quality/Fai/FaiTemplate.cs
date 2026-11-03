using System.Text.Json;
using System.Text.Json.Serialization;
using MiniMes.Domain.Common;
using MiniMes.Domain.MasterData;

namespace MiniMes.Domain.Quality.Fai;

public enum FaiCheckKind
{
    Auto,     // computed by the worksheet query (2.5b), QC cannot tick it
    Manual,   // QC looks and ticks
}

// The three automatic checks of spec 2.5b; an Auto item's Code is one of these names.
public enum FaiAutoCheck
{
    BomMatch,
    TestPassed,
    MeasurementsInSpec,
}

public sealed record FaiCheckItem(string Code, string Text, FaiCheckKind Kind);

// [v2] Spec M7 2.5b: the FAI checklist of an item. Versioned: a new version is a new row, the old one inactive, so
// a FaiRecord always shows the list it was judged against. The items are a small JSON list, read whole, never queried.
public class FaiTemplate : AuditableEntity, IAudited
{
    public const int TextMaxLength = 200;
    // "kind": "Auto" rather than 0: the JSON is read in SSMS too.
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public int Id { get; private set; }
    public int ItemId { get; private set; }
    public int Version { get; private set; }
    public string ItemsJson { get; private set; }
    public bool IsActive { get; private set; } = true;

    private FaiTemplate() { ItemsJson = "[]"; }

    public FaiTemplate(int itemId, int version, IReadOnlyList<FaiCheckItem> items)
    {
        if (version < 1) throw new DomainException("Version starts at 1.");
        if (items.Count == 0) throw new DomainException("A FAI checklist needs at least one item.");
        foreach (var i in items)
        {
            // An Auto item is one of the checks the worksheet knows how to compute, named as in the enum.
            if (i.Kind == FaiCheckKind.Auto && !Enum.GetNames<FaiAutoCheck>().Contains(i.Code))
                throw new DomainException($"'{i.Code}' is not an automatic check ({string.Join(", ", Enum.GetNames<FaiAutoCheck>())}).");
            if (i.Kind == FaiCheckKind.Manual) MasterDataRules.RequireCode(i.Code, "FAI item");
            if (string.IsNullOrWhiteSpace(i.Text) || i.Text.Length > TextMaxLength)
                throw new DomainException($"FAI item text is required and at most {TextMaxLength} characters.");
        }
        if (items.Select(i => i.Code).Distinct().Count() != items.Count) throw new DomainException("A FAI item code is listed twice.");

        ItemId = itemId;
        Version = version;
        ItemsJson = JsonSerializer.Serialize(items, Json);
    }

    public IReadOnlyList<FaiCheckItem> Items() => JsonSerializer.Deserialize<List<FaiCheckItem>>(ItemsJson, Json) ?? [];

    public void Deactivate() => IsActive = false;
}
