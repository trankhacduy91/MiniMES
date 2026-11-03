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

// Spec M7 2.5: the record of a nonconformance: what, how many, who proposed what, who approved. This step only
// creates it; the state machine (StartReview, Propose, Approve...) comes with its tests in step 8.
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
