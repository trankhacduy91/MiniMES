namespace MiniMes.Contracts.Quality;

// POST /api/quality/inspections (spec M7 2.3): ONE request = the scan of the board at its inspection step + the values
// measured there. The server judges OK/NG from the values (the client never decides) and records the scan through
// the M3 route check, so a board that is not at this step, or on Hold, cannot be measured.
//   Values          1..20 characteristics by plan code ("3V3_RAIL"), each at most 6 decimals (stored decimal(18,6))
//   Source          "Kiosk" (typed by the operator) | "Machine" (an automatic tester, the simulator)
//   ClientRequestId optional; the same id sent again (a retry after a timeout) returns the first answer, Duplicate
public sealed record RecordInspectionRequest(int StationId, string SerialNo, IReadOnlyList<InspectionValue> Values,
    string Source = "Kiosk", Guid? ClientRequestId = null);

public sealed record InspectionValue(string PlanCode, decimal Value);

// Always HTTP 200 for a business answer, like the scan:
//   Result      "Ok" | "Ng" (a value out of spec: the board went to the NG flow of M3) | "Blocked" (nothing written)
//   ReasonCode  the scan's reason when Blocked (SkippedStep, SerialHold...), null otherwise
//   Alerts      SPC rules that fired at these points and opened a NEW alert ("WE1", "WE4"); an alert already open for
//               the same rule is not repeated (Q5)
public sealed record InspectionResponse(string Result, string Message, string? ReasonCode, bool Duplicate,
    IReadOnlyList<MeasurementResultDto> Measurements, IReadOnlyList<SpcAlertDto> Alerts, int? NextStep, string? NextStepName);

public sealed record MeasurementResultDto(long Id, string PlanCode, decimal Value, string Result, decimal? Lsl, decimal? Usl);

public sealed record SpcAlertDto(long Id, int PlanId, string PlanCode, string Rule, long? MeasurementId, DateTime RaisedAt,
    string Status, int? NcrId);

// GET /api/quality/spc/{planId}/chart?take=100 (spec 2.4): the latest points, oldest first, with what to draw.
//   Limits      the active baseline (fixed, Q3); null = none yet: points only, no rules
//   Violations  rules firing at the shown points (re-evaluated, every point, so the chart can paint them red)
//   Window      statistics of the shown points: current Cp/Cpk and MR̄ (not the baseline)
public sealed record SpcChartDto(int PlanId, string PlanCode, string PlanName, string Unit, decimal? Lsl, decimal? Usl,
    decimal? Target, ControlLimitsDto? Limits, IReadOnlyList<SpcPointDto> Points, IReadOnlyList<SpcViolationDto> Violations,
    SpcWindowStatsDto? Window, IReadOnlyList<SpcAlertDto> OpenAlerts);

public sealed record ControlLimitsDto(int Id, decimal Mean, decimal Sigma, decimal Ucl, decimal Lcl, decimal? MrBar, decimal? UclMr,
    int SampleCount, DateTime? BaselineFrom, DateTime? BaselineTo, string Reason, string CalculatedBy, DateTime CalculatedAt);

// MovingRange = |this − previous shown point|; null for the first point.
public sealed record SpcPointDto(long MeasurementId, DateTime MeasuredAt, decimal Value, string Result, string? SerialNo,
    double? MovingRange);

public sealed record SpcViolationDto(long MeasurementId, string Rule);

public sealed record SpcWindowStatsDto(int Count, double Mean, double Sigma, double MrBar, double? Cp, double? Cpk);

// POST /api/quality/spc/{planId}/limits/recompute (QC, Q3): a new Phase I baseline from the points measured in
// [From, To), at least 20, with the reason. The previous baseline is kept as history.
public sealed record RecomputeLimitsRequest(DateTime From, DateTime To, string Reason);

// GET /api/quality/plans: the active characteristics, for the chart's plan picker. Source = "Item + operation" of a
// station plan (PCB-A / TEST) or the machine tag feeding it (L1-REFLOW-01/peakTemp).
public sealed record InspectionPlanDto(int Id, string Code, string Name, string Unit, decimal? Lsl, decimal? Usl, decimal? Target,
    string? ItemCode, string? OperationCode, string? MachineTag, bool HasBaseline);
