namespace MiniMes.Contracts.Oee;

// Spec M5 step 6: a stop the supervisor still has to explain. Times in UTC; the client shows plant time.
// Also pushed over SignalR (DowntimeNeedsClassification) when the machine ends such a stop.
public sealed record UnclassifiedDowntimeDto(
    long Id, int MachineId, string Machine, string Line, string State, DateTime StartAt, DateTime EndAt, double Minutes);

// Note: required with the reason OTHER.
public sealed record AssignDowntimeReasonRequest(int DowntimeReasonId, string? Note);

public sealed record DowntimeSegmentDto(
    long Id, int MachineId, string Machine, string State, DateTime StartAt, DateTime? EndAt,
    int? DowntimeReasonId, string? DowntimeReason, string? Note);
