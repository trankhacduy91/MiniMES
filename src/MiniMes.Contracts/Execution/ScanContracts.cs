namespace MiniMes.Contracts.Execution;

// POST /api/stations/{id}/scan, shared by the WPF kiosk (operator + barcode scanner) and the machine simulator
// (automatic tester): one endpoint, so every source goes through the same route check and the same log.
// Result: "OK" | "NG" (manual stations send OK); DefectCode required with NG. Source: "Kiosk" | "Machine".
public sealed record ScanRequest(string SerialNo, string Result = "OK", string? DefectCode = null, string Source = "Kiosk");

// Always HTTP 200 for a business answer, including Blocked (a normal daily event, not a protocol error):
//   Result     "Ok" | "Ng" (accepted and recorded) | "Blocked" (route check refused, nothing written)
//   ReasonCode null when accepted; otherwise a stable code (SkippedStep, MustGoRepair...) the client maps to .resx
//   Message    English fallback text for people
//   NextStep   Seq / operation the board goes to next; null when completed, NG (goes to Repair) or blocked
//   Duplicate  true = the same scan arrived again within a few seconds; answered like the first, nothing written
public sealed record ScanResponse(
    string Result,
    string Message,
    string? ReasonCode,
    int? NextStep,
    string? NextStepName,
    bool Duplicate);

// POST /api/stations/{id}/repair: the technician finishes a board checked in at this Repair station.
// ReturnToSeq null = retest the failed step; an earlier routing step = rework from there.
// ComponentReplaced required when Action starts with "Replace".
public sealed record RepairRequest(string SerialNo, string DefectCode, string Action, string? ComponentReplaced = null,
    int? ReturnToSeq = null);

// NextStep = the step route check now expects (the failed test, or the rework step).
public sealed record RepairResponse(string SerialNo, string Status, int? NextStep, string? NextStepName);

// POST /api/serials/{serialNo}/scrap (Supervisor).
public sealed record ScrapRequest(string Reason);

// POST /api/stations/{id}/sessions: the operator (badge login, JWT) starts working on a WO at this station.
// Closes the station's previous session, if any.
public sealed record OpenSessionRequest(int WorkOrderId);

public sealed record StationSessionDto(long Id, int StationId, string StationCode, int WorkOrderId, string WoNo,
    string OperatorCode, DateTime OpenedAt, DateTime? ClosedAt);
