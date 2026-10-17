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
