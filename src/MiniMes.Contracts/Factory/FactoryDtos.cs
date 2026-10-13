namespace MiniMes.Contracts.Factory;

// Plain DTOs: the rules (code pattern, lengths, station type) are FluentValidation validators in
// Application, reusing Domain FactoryRules, so there is one place that decides what is valid.

// ---------- read models ----------

public sealed record SiteDto(int Id, string Code, string Name, bool IsActive);
public sealed record AreaDto(int Id, int SiteId, string Code, string Name, bool IsActive);
public sealed record LineDto(int Id, int AreaId, string Code, string Name, bool IsActive);
public sealed record StationDto(int Id, int LineId, string Code, string Name, string Type, bool IsActive, int? WorkCenterId);

// ---------- create (Code is set once: it is part of MQTT topics, so it cannot be changed later) ----------

public class CreateFactoryItemRequest
{
    public string Code { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
}

public sealed class CreateSiteRequest : CreateFactoryItemRequest;

public sealed class CreateAreaRequest : CreateFactoryItemRequest
{
    public int SiteId { get; init; }
}

public sealed class CreateLineRequest : CreateFactoryItemRequest
{
    public int AreaId { get; init; }
}

public sealed class CreateStationRequest : CreateFactoryItemRequest
{
    public int LineId { get; init; }
    public string Type { get; init; } = string.Empty;
    public int? WorkCenterId { get; init; }   // M1: the routing operation this station does; null for Repair
}

// ---------- update (no Code; IsActive=false replaces delete) ----------

public class UpdateFactoryItemRequest
{
    public string Name { get; init; } = string.Empty;
    public bool IsActive { get; init; } = true;
}

public sealed class UpdateSiteRequest : UpdateFactoryItemRequest;
public sealed class UpdateAreaRequest : UpdateFactoryItemRequest;
public sealed class UpdateLineRequest : UpdateFactoryItemRequest;

public sealed class UpdateStationRequest : UpdateFactoryItemRequest
{
    public string Type { get; init; } = string.Empty;
    public int? WorkCenterId { get; init; }
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
