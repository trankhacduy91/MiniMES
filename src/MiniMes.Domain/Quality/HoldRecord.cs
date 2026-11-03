using MiniMes.Domain.Common;

namespace MiniMes.Domain.Quality;

// What the Hold was chosen by (spec 2.6 and [v2] (a)/(b)); with ScopeRef the history reads "held by pallet P26...".
public enum HoldScope
{
    Lot,            // a lot and every serial made from it (M6 forward trace)
    Serials,        // a hand-picked list
    Carton,         // [v2] the live serials of a carton (M3b)
    Pallet,         // [v2] ... of every carton on a pallet
    MachineWindow,  // [v2] serials scanned OK at the machine's work center in [from, to)
}

// Spec M7 2.6: one row per target held. The Status of the serial/lot is the single source of truth the gates read
// (route check M3, load verification M6); this table is why and by whom, and makes overlapping holds work (Q8):
// a target goes back to its old status only when it has no active row left. Bulk holds are written set-based by
// SQL (step 9); the constructor is for one target. Only the release columns are ever updated (trigger).
public class HoldRecord
{
    public const int ScopeRefMaxLength = 100, ReasonMaxLength = 200, ByMaxLength = 50;

    public long Id { get; private set; }
    public HoldTargetType TargetType { get; private set; }
    public long TargetId { get; private set; }
    public Guid BatchId { get; private set; }            // the rows of one Hold action, released together
    public HoldScope Scope { get; private set; }
    public string? ScopeRef { get; private set; }        // "L-260101-0001", "P261103000001", "L1-REFLOW-01 08:00-09:30"
    public string Reason { get; private set; }
    public int? NcrId { get; private set; }              // hold first, NCR later (containment)
    public string HeldBy { get; private set; }
    public DateTime HeldAt { get; private set; }
    public string? ReleasedBy { get; private set; }
    public DateTime? ReleasedAt { get; private set; }    // null = still active
    public string? ReleaseNote { get; private set; }

    private HoldRecord() { Reason = HeldBy = string.Empty; }

    public HoldRecord(HoldTargetType targetType, long targetId, Guid batchId, HoldScope scope, string? scopeRef,
        string reason, int? ncrId, string heldBy, DateTime utcNow)
    {
        if (targetId < 1) throw new DomainException("A target id must be positive.");
        if (batchId == Guid.Empty) throw new DomainException("A hold needs its batch id.");
        if (scopeRef?.Length > ScopeRefMaxLength) throw new DomainException($"Scope reference must be at most {ScopeRefMaxLength} characters.");
        ArgumentException.ThrowIfNullOrWhiteSpace(heldBy);
        TargetType = targetType;
        TargetId = targetId;
        BatchId = batchId;
        Scope = scope;
        ScopeRef = scopeRef;
        Reason = RequireText(reason, ReasonMaxLength, "reason");
        NcrId = ncrId;
        HeldBy = heldBy;
        HeldAt = utcNow;
    }

    public bool IsActive => ReleasedAt is null;

    public void Release(string by, string note, DateTime utcNow)
    {
        if (!IsActive) throw new DomainException("The hold is already released.");
        ArgumentException.ThrowIfNullOrWhiteSpace(by);
        var checkedNote = RequireText(note, ReasonMaxLength, "release note");
        ReleasedBy = by;
        ReleasedAt = utcNow;
        ReleaseNote = checkedNote;
    }

    private static string RequireText(string text, int max, string what)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Trim().Length > max)
            throw new DomainException($"A {what} is required, at most {max} characters.");
        return text.Trim();
    }
}
