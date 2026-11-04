using MiniMes.Domain.Common;

namespace MiniMes.Domain.Quality;

public enum NcrSource
{
    SpcAlert,
    Inspection,
    ScanNg,
    Traceability,
    Customer,
    Manual,
}

// [v2] 4 main states (spec 2.5); proposing a disposition is not a state of its own.
public enum NcrStatus
{
    Open,
    UnderReview,
    Dispositioned,
    Closed,
    Cancelled,
}

public enum NcrDisposition
{
    UseAsIs,
    Rework,
    Scrap,
    ReturnToVendor,   // lots only (Q13)
}

// What a Hold or an NCR points at. Cartons and pallets are expanded into their serials (Q19).
public enum HoldTargetType
{
    Serial,
    Lot,
}

// Spec M7 2.5: the record of a nonconformance: what, how many, who proposed what, who approved.
// [v2] Open -> UnderReview -> Dispositioned -> Closed; Cancelled from Open or UnderReview. Proposing a disposition
// is not a state: QC may change the proposal while UnderReview. The effects on serials / lots (release, repair,
// scrap, return) are applied by the Approve use case, not here: Ncr knows neither Serial nor MaterialLot (step 9).
public class Ncr : AuditableEntity, IAudited
{
    public const int NoMaxLength = 20, RefMaxLength = 50, DescriptionMaxLength = 500, NoteMaxLength = 500;
    public const long MaxSequence = 999_999;

    public int Id { get; private set; }
    public string NcrNo { get; private set; }
    public NcrSource Source { get; private set; }
    public string? SourceRef { get; private set; }       // the alert id, the scan, the customer complaint number
    public int? DefectCodeId { get; private set; }
    public string Description { get; private set; }      // what was seen, in the words of the person who saw it
    public int Qty { get; private set; }
    public NcrStatus Status { get; private set; } = NcrStatus.Open;
    public NcrDisposition? Disposition { get; private set; }
    public string? DispositionNote { get; private set; }
    public string? ConcessionRef { get; private set; }   // UseAsIs: the customer's written deviation
    public string? CapaReference { get; private set; }   // CAPA is out of scope (B10): only a reference
    public string? ReviewedBy { get; private set; }
    public string? ProposedBy { get; private set; }
    public DateTime? ProposedAt { get; private set; }
    public string? ApprovedBy { get; private set; }
    public DateTime? ApprovedAt { get; private set; }
    public string? ClosedBy { get; private set; }
    public DateTime? ClosedAt { get; private set; }
    public string? CancelReason { get; private set; }
    public byte[] RowVersion { get; private set; } = [];
    public List<NcrItem> Items { get; private set; } = [];

    private Ncr() { NcrNo = Description = string.Empty; }

    public Ncr(string ncrNo, NcrSource source, string? sourceRef, int? defectCodeId, string description, int qty,
        IReadOnlyCollection<(HoldTargetType Type, long Id)> targets)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ncrNo);
        if (ncrNo.Length > NoMaxLength) throw new DomainException($"NCR number must be at most {NoMaxLength} characters.");
        if (sourceRef?.Length > RefMaxLength) throw new DomainException($"Source reference must be at most {RefMaxLength} characters.");
        if (string.IsNullOrWhiteSpace(description) || description.Trim().Length > DescriptionMaxLength)
            throw new DomainException($"A description is required, at most {DescriptionMaxLength} characters.");
        if (qty < 1) throw new DomainException("Quantity must be at least 1.");
        if (targets.Count == 0) throw new DomainException("An NCR needs at least one serial or lot.");
        if (targets.Distinct().Count() != targets.Count) throw new DomainException("A target is listed twice.");

        NcrNo = ncrNo;
        Source = source;
        SourceRef = string.IsNullOrWhiteSpace(sourceRef) ? null : sourceRef.Trim();
        DefectCodeId = defectCodeId;
        Description = description.Trim();
        Qty = qty;
        Items = targets.Select(t => new NcrItem(t.Type, t.Id)).ToList();
    }

    // QC takes the case: the reviewer is recorded (when is in the audit log, Ncr is IAudited).
    public void StartReview(string by)
    {
        Require(NcrStatus.Open, "start the review");
        ReviewedBy = RequireBy(by);
        Status = NcrStatus.UnderReview;
    }

    // Q13: return to vendor only when every target is a lot (a serial is our own product). Q14: use-as-is is the
    // riskiest decision: the note is required (for every disposition), the concession reference when the customer
    // has to accept a deviation. Replaces any earlier proposal.
    public void ProposeDisposition(NcrDisposition disposition, string note, string? concessionRef, string by, DateTime utcNow)
    {
        Require(NcrStatus.UnderReview, "propose a disposition");
        var checkedNote = RequireText(note, NoteMaxLength, "disposition note");
        if (disposition == NcrDisposition.ReturnToVendor && Items.Any(i => i.TargetType != HoldTargetType.Lot))
            throw new DomainException("Return to vendor applies to lots only: this NCR has serials.");
        if (concessionRef?.Trim().Length > RefMaxLength) throw new DomainException($"Concession reference must be at most {RefMaxLength} characters.");
        if (disposition != NcrDisposition.UseAsIs && !string.IsNullOrWhiteSpace(concessionRef))
            throw new DomainException("A concession reference belongs to a use-as-is decision.");
        Disposition = disposition;
        DispositionNote = checkedNote;
        ConcessionRef = string.IsNullOrWhiteSpace(concessionRef) ? null : concessionRef.Trim();
        ProposedBy = RequireBy(by);
        ProposedAt = utcNow;
    }

    // Q11: segregation of duties. The approver is another person than the proposer, compared by user code
    // (employee code from the JWT), never by display name.
    public void Approve(string by, DateTime utcNow)
    {
        Require(NcrStatus.UnderReview, "approve");
        if (Disposition is null) throw new DomainException("No disposition has been proposed yet.");
        if (string.Equals(RequireBy(by), ProposedBy, StringComparison.OrdinalIgnoreCase))
            throw new DomainException("The approver must be another person than the one who proposed the disposition.");
        ApprovedBy = by;
        ApprovedAt = utcNow;
        Status = NcrStatus.Dispositioned;
    }

    // The approver disagrees: the proposal is wiped, the NCR stays UnderReview for a new one.
    public void RejectProposal(string by)
    {
        Require(NcrStatus.UnderReview, "reject the proposal");
        RequireBy(by);
        if (Disposition is null) throw new DomainException("There is no proposal to reject.");
        (Disposition, DispositionNote, ConcessionRef, ProposedBy, ProposedAt) = (null, null, null, null, null);
    }

    // Q12: not while a hold placed for this NCR is still active (the count comes from the use case).
    public void Close(string by, int activeHolds, string? capaReference, DateTime utcNow)
    {
        Require(NcrStatus.Dispositioned, "close");
        if (activeHolds > 0) throw new DomainException($"{activeHolds} hold(s) of this NCR are still active: release them first.");
        if (capaReference?.Trim().Length > RefMaxLength) throw new DomainException($"CAPA reference must be at most {RefMaxLength} characters.");
        CapaReference = string.IsNullOrWhiteSpace(capaReference) ? null : capaReference.Trim();
        ClosedBy = RequireBy(by);
        ClosedAt = utcNow;
        Status = NcrStatus.Closed;
    }

    // Opened by mistake or a duplicate. Not once a disposition is approved (its effects are applied); the holds
    // it placed must be released first, or the goods stay blocked with no open record behind them.
    public void Cancel(string by, string reason, int activeHolds, DateTime utcNow)
    {
        if (Status is not (NcrStatus.Open or NcrStatus.UnderReview))
            throw new DomainException($"NCR {NcrNo} is {Status}: only an Open or UnderReview NCR can be cancelled.");
        if (activeHolds > 0) throw new DomainException($"{activeHolds} hold(s) of this NCR are still active: release them first.");
        var checkedReason = RequireText(reason, NoteMaxLength, "cancel reason");
        ClosedBy = RequireBy(by);
        ClosedAt = utcNow;
        CancelReason = checkedReason;
        Status = NcrStatus.Cancelled;
    }

    private void Require(NcrStatus expected, string action)
    {
        if (Status != expected) throw new DomainException($"NCR {NcrNo} is {Status}: cannot {action}.");
    }

    private static string RequireBy(string by)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(by);
        return by;
    }

    private static string RequireText(string text, int max, string what)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Trim().Length > max)
            throw new DomainException($"A {what} is required, at most {max} characters.");
        return text.Trim();
    }

    // NCR-000001: from the NcrSeq SEQUENCE, 6 digits, never reset (like lots and cartons; not MAX + 1).
    public static string FormatNo(long sequence)
    {
        if (sequence is < 1 or > MaxSequence) throw new DomainException($"NCR sequence {sequence} is out of range 1-{MaxSequence}.");
        return $"NCR-{sequence:D6}";
    }
}

public class NcrItem
{
    public int Id { get; private set; }
    public int NcrId { get; private set; }
    public HoldTargetType TargetType { get; private set; }
    public long TargetId { get; private set; }           // Serials.Id or MaterialLots.Id (step 1 decision 1)

    private NcrItem() { }

    internal NcrItem(HoldTargetType targetType, long targetId)
    {
        if (targetId < 1) throw new DomainException("A target id must be positive.");
        TargetType = targetType;
        TargetId = targetId;
    }
}
