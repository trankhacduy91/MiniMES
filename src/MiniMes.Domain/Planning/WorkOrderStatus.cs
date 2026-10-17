namespace MiniMes.Domain.Planning;

// Life cycle of a work order (spec M2 1.2c). Stored as text, so adding a value never shifts existing rows.
public enum WorkOrderStatus
{
    Draft,       // planner is still editing; nothing is frozen
    Released,    // snapshot taken, serials created, the line may start
    InProgress,  // first serial scanned at the first step (M3 calls Start)
    OnHold,      // stopped by a supervisor, can be resumed
    Completed,   // production finished (planned qty reached or closed by hand with a reason)
    Closed,      // books closed: read-only
    Cancelled,   // never ran, or stopped before any output
}

// What a user can ask for; the API returns the allowed ones so the UI never repeats the rules (spec 2.6).
public enum WorkOrderAction
{
    Edit,
    Release,
    Start,
    Hold,
    Resume,
    Complete,
    Close,
    Cancel,
    ChangePriority,
}

// [v2] First Article Inspection gate (spec 1.2f2). Null on a Draft: decided at Release from RequireFai.
// No "Failed": a failed FAI keeps the WO Pending and the next serial becomes the new sample.
public enum FaiStatus
{
    NotRequired,
    Pending,
    Passed,
}
