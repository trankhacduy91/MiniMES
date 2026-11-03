using MiniMes.Domain.Common;

namespace MiniMes.Domain.Quality;

// Spec M7 1.2 / Q3: the Phase I baseline of a characteristic (mean, σ and the limits from them). Fixed: new
// measurements never move it, otherwise a slow drift drags its own limits along and is never seen. QC recomputes
// it on purpose, with a reason: a new row, the old one kept as history (IsActive = false).
public class ControlLimitSet : IAudited
{
    public const int ReasonMaxLength = 200;

    public int Id { get; private set; }
    public int InspectionPlanId { get; private set; }
    public decimal Mean { get; private set; }
    public decimal Sigma { get; private set; }
    public decimal Ucl { get; private set; }             // mean ± 3σ, stored: the chart and the audit show what was used
    public decimal Lcl { get; private set; }
    public decimal? MrBar { get; private set; }          // [v2] MR chart; null when the baseline came without data (seed)
    public decimal? UclMr { get; private set; }
    public int SampleCount { get; private set; }
    public DateTime? BaselineFrom { get; private set; }
    public DateTime? BaselineTo { get; private set; }
    public string Reason { get; private set; }
    public string CalculatedBy { get; private set; }
    public DateTime CalculatedAt { get; private set; }
    public bool IsActive { get; private set; } = true;

    private ControlLimitSet() { Reason = CalculatedBy = string.Empty; }

    public ControlLimitSet(int inspectionPlanId, decimal mean, decimal sigma, decimal? mrBar, int sampleCount,
        DateTime? baselineFrom, DateTime? baselineTo, string reason, string calculatedBy, DateTime utcNow)
    {
        // σ = 0 would divide z by zero in the rule engine; it also means the data is wrong (all points equal).
        if (sigma <= 0) throw new DomainException("Sigma must be greater than 0.");
        if (mrBar < 0) throw new DomainException("The average moving range cannot be negative.");
        if (sampleCount < 0) throw new DomainException("Sample count cannot be negative.");
        if (baselineFrom > baselineTo) throw new DomainException("The baseline period ends before it starts.");
        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length > ReasonMaxLength)
            throw new DomainException($"A reason is required, at most {ReasonMaxLength} characters.");
        ArgumentException.ThrowIfNullOrWhiteSpace(calculatedBy);

        InspectionPlanId = inspectionPlanId;
        Mean = mean;
        Sigma = sigma;
        Ucl = mean + 3 * sigma;
        Lcl = mean - 3 * sigma;
        MrBar = mrBar;
        UclMr = mrBar * D4;
        SampleCount = sampleCount;
        BaselineFrom = baselineFrom;
        BaselineTo = baselineTo;
        Reason = reason.Trim();
        CalculatedBy = calculatedBy;
        CalculatedAt = utcNow;
    }

    // D4 for moving ranges of 2 points (spec 1.2: UCL_MR = 3.267 × MR̄).
    public const decimal D4 = 3.267m;

    public void Deactivate() => IsActive = false;
}
