namespace MiniMes.Contracts.MasterData;

public sealed record RoutingSummaryDto(
    int Id, int ItemId, string ItemCode, int Version, string Status,
    DateTime? EffectiveFrom, DateTime? EffectiveTo, int StepCount);

public sealed record RoutingStepDto(
    int Seq, string OperationCode, int WorkCenterId, string WorkCenterCode,
    decimal IdealCycleTimeSec, bool IsInspection, bool IsMandatory, string? SopUrl);

public sealed record RoutingDto(
    int Id, int ItemId, string ItemCode, int Version, string Status,
    DateTime? EffectiveFrom, DateTime? EffectiveTo, IReadOnlyList<RoutingStepDto> Steps);

public sealed record RoutingStepInput(
    int Seq, string OperationCode, int WorkCenterId, decimal IdealCycleTimeSec,
    bool IsInspection, bool IsMandatory, string? SopUrl = null);

// The full list of steps: saving replaces every step of the draft.
public sealed record SaveRoutingStepsRequest(IReadOnlyList<RoutingStepInput> Steps);
