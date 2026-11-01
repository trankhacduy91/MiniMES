using MiniMes.Domain.Common;

namespace MiniMes.Domain.Material;

public enum IqcResult
{
    Pass,
    Fail,
}

// [v2] Spec M6 1.2b / R16: one incoming inspection of a supplier lot (all its reels pending at that time).
// Insert-only: the record an auditor reads ("who inspected this delivery, how many samples").
public class IqcRecord
{
    public const int NoteMaxLength = 500;
    public const string DefectCategory = "Incoming";   // M1 defect codes of this category are the IQC reasons

    public long Id { get; private set; }
    public int ItemId { get; private set; }
    public string SupplierLot { get; private set; }
    public int LotCount { get; private set; }            // reels changed by this result
    public int SampleQty { get; private set; }
    public int DefectQty { get; private set; }
    public IqcResult Result { get; private set; }
    public string? DefectCode { get; private set; }      // required when Fail (an Incoming defect code of M1)
    public string? Note { get; private set; }
    public string InspectedBy { get; private set; }
    public DateTime InspectedAt { get; private set; }

    private IqcRecord() { SupplierLot = InspectedBy = string.Empty; }

    public IqcRecord(int itemId, string supplierLot, int lotCount, int sampleQty, int defectQty, IqcResult result,
        string? defectCode, string? note, string inspectedBy, DateTime inspectedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(supplierLot);
        ArgumentException.ThrowIfNullOrWhiteSpace(inspectedBy);
        if (lotCount <= 0) throw new DomainException("An IQC result must change at least one lot.");
        if (sampleQty <= 0 || defectQty < 0 || defectQty > sampleQty)
            throw new DomainException("Samples must be greater than 0 and defects between 0 and the samples.");
        if (result == IqcResult.Fail && string.IsNullOrWhiteSpace(defectCode))
            throw new DomainException("A failed IQC needs a defect code.");
        if (note?.Trim().Length > NoteMaxLength) throw new DomainException($"Note must be at most {NoteMaxLength} characters.");

        ItemId = itemId;
        SupplierLot = supplierLot.Trim();
        LotCount = lotCount;
        SampleQty = sampleQty;
        DefectQty = defectQty;
        Result = result;
        DefectCode = string.IsNullOrWhiteSpace(defectCode) ? null : defectCode.Trim();
        Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        InspectedBy = inspectedBy;
        InspectedAt = inspectedAt;
    }
}
