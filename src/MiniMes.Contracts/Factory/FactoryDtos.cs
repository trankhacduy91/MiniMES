using System.ComponentModel.DataAnnotations;

namespace MiniMes.Contracts.Factory;

// Same rule as Domain FactoryRules: checked here so the API answers 400 before any business logic.
public static class CodeRules
{
    public const string Pattern = "^[A-Z0-9-]+$";
    public const string Message = "Code must contain only A-Z, 0-9 or '-' (no spaces): it is used in MQTT topics.";
}

// ---------- read models ----------

public sealed record SiteDto(int Id, string Code, string Name, bool IsActive);
public sealed record AreaDto(int Id, int SiteId, string Code, string Name, bool IsActive);
public sealed record LineDto(int Id, int AreaId, string Code, string Name, bool IsActive);
public sealed record StationDto(int Id, int LineId, string Code, string Name, string Type, bool IsActive);

// ---------- create (Code is set once: it is part of MQTT topics, so it cannot be changed later) ----------

public class CreateFactoryItemRequest
{
    [Required, StringLength(50), RegularExpression(CodeRules.Pattern, ErrorMessage = CodeRules.Message)]
    public string Code { get; init; } = string.Empty;

    [Required, StringLength(200)]
    public string Name { get; init; } = string.Empty;
}

public sealed class CreateSiteRequest : CreateFactoryItemRequest;

public sealed class CreateAreaRequest : CreateFactoryItemRequest
{
    [Range(1, int.MaxValue)]
    public int SiteId { get; init; }
}

public sealed class CreateLineRequest : CreateFactoryItemRequest
{
    [Range(1, int.MaxValue)]
    public int AreaId { get; init; }
}

public sealed class CreateStationRequest : CreateFactoryItemRequest
{
    [Range(1, int.MaxValue)]
    public int LineId { get; init; }

    [Required, AllowedValues(StationTypes.Smt, StationTypes.Assembly, StationTypes.Test, StationTypes.Repair, StationTypes.Pack)]
    public string Type { get; init; } = string.Empty;
}

// ---------- update (no Code; IsActive=false replaces delete) ----------

public class UpdateFactoryItemRequest
{
    [Required, StringLength(200)]
    public string Name { get; init; } = string.Empty;

    public bool IsActive { get; init; } = true;
}

public sealed class UpdateSiteRequest : UpdateFactoryItemRequest;
public sealed class UpdateAreaRequest : UpdateFactoryItemRequest;
public sealed class UpdateLineRequest : UpdateFactoryItemRequest;

public sealed class UpdateStationRequest : UpdateFactoryItemRequest
{
    [Required, AllowedValues(StationTypes.Smt, StationTypes.Assembly, StationTypes.Test, StationTypes.Repair, StationTypes.Pack)]
    public string Type { get; init; } = string.Empty;
}

// Mirrors Domain StationType names (Contracts cannot reference Domain); a unit test keeps them in sync.
public static class StationTypes
{
    public const string Smt = "Smt";
    public const string Assembly = "Assembly";
    public const string Test = "Test";
    public const string Repair = "Repair";
    public const string Pack = "Pack";

    public static readonly IReadOnlyList<string> All = [Smt, Assembly, Test, Repair, Pack];
}
