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
