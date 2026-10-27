namespace MiniMes.Contracts.Andon;

// Spec M4 2.9. Type and Status travel as names ("Machine", "Raised"), like the work order status.
public static class AndonTypes
{
    public const string Machine = nameof(Machine);
    public const string Material = nameof(Material);
    public const string Quality = nameof(Quality);
    public const string Other = nameof(Other);

    public static readonly IReadOnlyList<string> All = [Machine, Material, Quality, Other];
}

// Also pushed over SignalR (AndonChanged) after every change, to the group of the machine's line.
// RowVersion is sent back on acknowledge / resolve / cancel: the second of two supervisors gets 409.
public sealed record AndonDto(
    long Id, int MachineId, string Machine, string Line, string Type, string Status,
    string RaisedBy, DateTime RaisedAt, string? AckBy, DateTime? AckAt,
    string? ResolvedBy, DateTime? ResolvedAt, int? DowntimeReasonId, string? DowntimeReason, string? ResolutionNote,
    int EscalationLevel, string RowVersion);

// Created = false: a call of this type was already open for the machine; that one is returned, no second call.
public sealed record RaiseAndonResult(AndonDto Andon, bool Created);

public sealed record RaiseAndonRequest(int MachineId, string Type);

public sealed record AndonTransitionRequest(string RowVersion);

public sealed record ResolveAndonRequest(int? DowntimeReasonId, string? Note, string RowVersion);

// [v2] GET /api/andon/stats?from=&to= (step 7b): calls raised in [From, To), Cancelled left out (raised by mistake).
// ByLevel = how far calls climbed before someone acknowledged them (the final EscalationLevel).
// AvgReactionSeconds = mean of AckAt - RaisedAt over the calls that were acknowledged; null when none.
public sealed record AndonStatsDto(DateTime From, DateTime To, int Total, IReadOnlyList<AndonLevelCountDto> ByLevel,
    int Acknowledged, double? AvgReactionSeconds);

public sealed record AndonLevelCountDto(int Level, int Count);
