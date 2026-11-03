using MiniMes.Domain.Common;

namespace MiniMes.Domain.Quality.Fai;

public enum FaiResult
{
    Pass,
    Fail,
}

// [v2] Spec M7 2.5b: QC's judgement of the first article of a WO. Insert-only (trigger): a second FAI after a Fail
// is a new row with a new sample board. The worksheet use case (step 8b) builds the checklist JSON.
public class FaiRecord
{
    public const int NoteMaxLength = 500, ByMaxLength = 50;

    public long Id { get; private set; }
    public int WorkOrderId { get; private set; }
    public long SerialId { get; private set; }           // the sample board
    public int FaiTemplateId { get; private set; }       // the version judged against
    public string ChecklistJson { get; private set; }    // each item with its outcome (auto: computed, manual: ticked)
    public bool AutoChecksPassed { get; private set; }
    public FaiResult Result { get; private set; }
    public string? Note { get; private set; }
    public string InspectedBy { get; private set; }
    public DateTime InspectedAt { get; private set; }

    private FaiRecord() { ChecklistJson = InspectedBy = string.Empty; }

    public FaiRecord(int workOrderId, long serialId, int faiTemplateId, string checklistJson, bool autoChecksPassed,
        FaiResult result, string? note, string inspectedBy, DateTime utcNow)
    {
        // Q17: a red automatic check cannot be overruled by a person ticking Pass.
        if (result == FaiResult.Pass && !autoChecksPassed)
            throw new DomainException("FAI cannot pass while an automatic check is red.");
        if (note?.Trim().Length > NoteMaxLength) throw new DomainException($"Note must be at most {NoteMaxLength} characters.");
        ArgumentException.ThrowIfNullOrWhiteSpace(checklistJson);
        ArgumentException.ThrowIfNullOrWhiteSpace(inspectedBy);

        WorkOrderId = workOrderId;
        SerialId = serialId;
        FaiTemplateId = faiTemplateId;
        ChecklistJson = checklistJson;
        AutoChecksPassed = autoChecksPassed;
        Result = result;
        Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        InspectedBy = inspectedBy;
        InspectedAt = utcNow;
    }
}
