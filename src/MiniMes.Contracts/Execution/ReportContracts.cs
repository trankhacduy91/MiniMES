namespace MiniMes.Contracts.Execution;

// GET /api/reports/fpy?workOrderId=: first pass yield per routing step, from the FIRST scan of each serial at
// each step (a retest after Repair is not "first pass"). Percentages are null when nothing entered the step.
public sealed record FpyReportDto(
    int WorkOrderId,
    string WoNo,
    IReadOnlyList<FpyStepDto> Steps,
    decimal? RtyPct,          // product of the steps' FPY
    int Completed,
    int Scrapped,
    decimal? FinalYieldPct);  // Completed / (Completed + Scrapped)

public sealed record FpyStepDto(int Seq, string OperationCode, int Entered, int PassedFirstTime, decimal? FpyPct);

// GET /api/reports/wip?workOrderId=: boards started and not finished, grouped by where they are.
public sealed record WipReportDto(int WorkOrderId, string WoNo, IReadOnlyList<WipRowDto> Rows, int Total);

// LastStep = the step passed (CurrentSeq), "-" before the first; NextStep = where the board goes next.
public sealed record WipRowDto(int CurrentSeq, string LastStep, string? NextStep, string Status, int Qty);
