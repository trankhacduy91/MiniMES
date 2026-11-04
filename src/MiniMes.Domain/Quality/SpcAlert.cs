using MiniMes.Domain.Common;

namespace MiniMes.Domain.Quality;

// Stored as text (varchar): the filtered unique index reads "[Status] = 'Open'", and SSMS shows "WE1".
public enum SpcRuleCode
{
    WE1,        // 1 point beyond 3σ
    WE2,        // 2 of 3 beyond 2σ, same side
    WE3,        // 4 of 5 beyond 1σ, same side (optional, off by default)
    WE4,        // run: 7 points on one side of the center line (RunLength configurable)
    OutOfSpec,  // [v2] a machine sample beyond USL/LSL (Q18: an alert + Andon, no board to NG)
    DataGap,    // [v2] 3 sampling periods without a Good value (2.3b): shown, never filled with made-up points
}

public enum SpcAlertStatus
{
    Open,
    Closed,
}

// Spec M7 2.3: one rule violation, tied to the point that fired it. Q5: while one is Open for (plan, rule), the
// same rule firing again adds nothing (filtered unique index; the loser of a race is ignored, not an error).
public class SpcAlert
{
    public const int ClosedByMaxLength = 50, CloseNoteMaxLength = 500;

    public long Id { get; private set; }
    public int InspectionPlanId { get; private set; }
    public SpcRuleCode RuleCode { get; private set; }
    public long? MeasurementId { get; private set; }     // null for DataGap: there is no point
    public DateTime RaisedAt { get; private set; }
    public SpcAlertStatus Status { get; private set; } = SpcAlertStatus.Open;
    public int? NcrId { get; private set; }
    public string? ClosedBy { get; private set; }
    public DateTime? ClosedAt { get; private set; }
    public string? CloseNote { get; private set; }

    private SpcAlert() { }

    public SpcAlert(int inspectionPlanId, SpcRuleCode ruleCode, long? measurementId, DateTime utcNow)
    {
        if (measurementId is null && ruleCode != SpcRuleCode.DataGap)
            throw new DomainException($"Rule {ruleCode} fires at a measured point.");
        InspectionPlanId = inspectionPlanId;
        RuleCode = ruleCode;
        MeasurementId = measurementId;
        RaisedAt = utcNow;
    }

    public void LinkNcr(int ncrId)
    {
        if (Status != SpcAlertStatus.Open) throw new DomainException("The alert is already closed.");
        if (NcrId is not null) throw new DomainException($"The alert already has NCR {NcrId}.");
        NcrId = ncrId;
    }

    // The NCR opened from it was cancelled (opened by mistake): the alert is free for another NCR again.
    public void UnlinkNcr(int ncrId)
    {
        if (NcrId != ncrId) throw new DomainException($"The alert is not linked to NCR {ncrId}.");
        NcrId = null;
    }

    // Closed when its NCR closes, or when QC marks it handled and writes what was done (spec 2.5 trap 5).
    public void Close(string by, string note, DateTime utcNow)
    {
        if (Status != SpcAlertStatus.Open) throw new DomainException("The alert is already closed.");
        ArgumentException.ThrowIfNullOrWhiteSpace(by);
        if (string.IsNullOrWhiteSpace(note) || note.Trim().Length > CloseNoteMaxLength)
            throw new DomainException($"A note is required, at most {CloseNoteMaxLength} characters.");
        Status = SpcAlertStatus.Closed;
        ClosedBy = by;
        ClosedAt = utcNow;
        CloseNote = note.Trim();
    }
}
