using MiniMes.Domain.Common;

namespace MiniMes.Domain.Quality;

public enum MeasurementSource
{
    Manual,     // typed by QC at the BackOffice
    Station,    // sent by a test station (simulator, M4)
    Machine,    // [v2] sampled from a machine tag (2.3b): no board
}

// Spec M7 2.3: one value of one characteristic. Insert-only (Q16, trigger): a wrong value is answered by a
// correction row pointing at it, never by an UPDATE. The limits used are copied in (step 1 decision 2), so the
// result stays what it was when the spec changes later. Not audited: high volume, the row itself is the record.
public class Measurement
{
    public const int MeasuredByMaxLength = 50, CorrectionReasonMaxLength = 200;

    public long Id { get; private set; }
    public int InspectionPlanId { get; private set; }
    public MeasurementSource Source { get; private set; }
    public long? SerialId { get; private set; }          // null for a machine sample (Q18)
    public int? StationId { get; private set; }
    public int? MachineTagId { get; private set; }
    public decimal Value { get; private set; }
    public decimal? Usl { get; private set; }            // snapshot of the plan at measuring time
    public decimal? Lsl { get; private set; }
    public MeasureResult Result { get; private set; }
    public DateTime MeasuredAt { get; private set; }     // the order of the chart and of the rules (2.3 trap 1)
    public DateTime? SourceTs { get; private set; }      // machine sample: the ProcessValue time, unique per tag
    public string? MeasuredBy { get; private set; }
    public Guid? ClientRequestId { get; private set; }   // a station resending the same value is stored once
    public long? CorrectsMeasurementId { get; private set; }
    public string? CorrectionReason { get; private set; }

    private Measurement() { }

    private Measurement(InspectionPlan plan, MeasurementSource source, decimal value, DateTime measuredAt)
    {
        if (!plan.IsActive) throw new DomainException($"Inspection plan {plan.Code} is not active.");
        InspectionPlanId = plan.Id;
        Source = source;
        Value = value;
        Usl = plan.Usl;
        Lsl = plan.Lsl;
        Result = plan.Judge(value);                       // the server decides, never the client (2.3)
        MeasuredAt = measuredAt;
    }

    public static Measurement OfSerial(InspectionPlan plan, MeasurementSource source, long serialId, int stationId,
        decimal value, string measuredBy, DateTime utcNow, Guid? clientRequestId = null)
    {
        if (source == MeasurementSource.Machine) throw new DomainException("A machine sample has no serial.");
        ArgumentException.ThrowIfNullOrWhiteSpace(measuredBy);
        if (measuredBy.Length > MeasuredByMaxLength) throw new DomainException($"Measured by must be at most {MeasuredByMaxLength} characters.");
        if (clientRequestId == Guid.Empty) throw new DomainException("A client request id cannot be empty.");
        return new Measurement(plan, source, value, utcNow)
        {
            SerialId = serialId,
            StationId = stationId,
            MeasuredBy = measuredBy,
            ClientRequestId = clientRequestId,
        };
    }

    // [v2] 2.3b: the point is placed at the time the machine read it, not when the sampler ran.
    public static Measurement FromMachine(InspectionPlan plan, int machineTagId, decimal value, DateTime sourceTs)
        => new(plan, MeasurementSource.Machine, value, sourceTs) { MachineTagId = machineTagId, SourceTs = sourceTs };
}
