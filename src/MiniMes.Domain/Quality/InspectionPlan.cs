using MiniMes.Domain.Common;
using MiniMes.Domain.MasterData;

namespace MiniMes.Domain.Quality;

public enum MeasureResult
{
    Ok,
    Ng,
}

// Spec M7 1.2: one characteristic to measure ("3V3_RAIL" at TEST of PCB-A, "REFLOW_PEAK_TEMP" of an oven).
// Stable row (step 1 decision 2): a spec change edits USL/LSL here, audited; every Measurement keeps a copy of the
// limits it was judged against, so history never changes (Q15). Control limits are NOT here: they belong to the
// process, not to the customer (ControlLimitSet).
public class InspectionPlan : AuditableEntity, IAudited
{
    public const int UnitMaxLength = 20, FrequencyMaxLength = 100;

    public int Id { get; private set; }
    public string Code { get; private set; }
    public string Name { get; private set; }
    public int? ItemId { get; private set; }             // station plan: the item + operation code measured
    public string? OperationCode { get; private set; }   // (step 1 decision 3); null for a machine plan (via MachineTag)
    public string Unit { get; private set; }
    public decimal? Target { get; private set; }
    public decimal? Usl { get; private set; }            // null = one-sided (flatness has only an upper limit)
    public decimal? Lsl { get; private set; }
    public int SampleSize { get; private set; } = 1;     // MVP: I-MR chart, one value per point
    public string? Frequency { get; private set; }       // "every board", "every 30 s": read by people
    public int? DefectCodeId { get; private set; }       // NG code when a board is out of spec (Q1)
    public bool IsActive { get; private set; } = true;
    public byte[] RowVersion { get; private set; } = [];

    private InspectionPlan() { Code = Name = Unit = string.Empty; }

    private InspectionPlan(string code, string name, string unit, decimal? lsl, decimal? usl, decimal? target)
    {
        MasterDataRules.RequireCode(code, "Inspection plan");
        if (string.IsNullOrWhiteSpace(name) || name.Length > MasterDataRules.NameMaxLength)
            throw new DomainException($"Name is required and at most {MasterDataRules.NameMaxLength} characters.");
        if (string.IsNullOrWhiteSpace(unit) || unit.Length > UnitMaxLength)
            throw new DomainException($"Unit is required and at most {UnitMaxLength} characters.");
        Code = code;
        Name = name.Trim();
        Unit = unit;
        ChangeSpec(lsl, usl, target);
    }

    // Measured on a board at a station: out of spec makes the board NG, so it needs the defect code (Q1).
    public static InspectionPlan ForStation(string code, string name, int itemId, string operationCode, string unit,
        decimal? lsl, decimal? usl, decimal? target, int defectCodeId, string? frequency = null)
    {
        var plan = new InspectionPlan(code, name, unit, lsl, usl, target);
        MasterDataRules.RequireCode(operationCode, "Operation");
        plan.ItemId = itemId;
        plan.OperationCode = operationCode;
        plan.DefectCodeId = defectCodeId;
        plan.Frequency = CheckFrequency(frequency);
        return plan;
    }

    // [v2] Sampled from a machine tag (2.3b): no board, so no NG; out of spec raises an alert + Andon (Q18).
    public static InspectionPlan ForMachine(string code, string name, string unit, decimal? lsl, decimal? usl,
        decimal? target, string? frequency = null)
        => new(code, name, unit, lsl, usl, target) { Frequency = CheckFrequency(frequency) };

    // Q15: audited; the measurements already taken keep the limits they were judged against.
    public void ChangeSpec(decimal? lsl, decimal? usl, decimal? target)
    {
        if (lsl is null && usl is null) throw new DomainException("A characteristic needs at least one specification limit.");
        if (lsl >= usl) throw new DomainException("The lower specification limit must be below the upper one.");
        if (target is { } t && (t < lsl || t > usl)) throw new DomainException("The target must be inside the specification limits.");
        Lsl = lsl;
        Usl = usl;
        Target = target;
    }

    // Q1/Q2: decimal comparison, a value exactly on the limit is OK.
    public MeasureResult Judge(decimal value)
        => value > Usl || value < Lsl ? MeasureResult.Ng : MeasureResult.Ok;

    public void SetActive(bool isActive) => IsActive = isActive;

    private static string? CheckFrequency(string? frequency)
    {
        if (frequency?.Length > FrequencyMaxLength)
            throw new DomainException($"Frequency must be at most {FrequencyMaxLength} characters.");
        return string.IsNullOrWhiteSpace(frequency) ? null : frequency.Trim();
    }
}
