using MiniMes.Domain.Common;

namespace MiniMes.Domain.Machines;

// [v2] Spec M4 2.3b: a process parameter a machine publishes (reflow peak temperature, conveyor speed...).
// Master data: only tags listed here are accepted. The plausible range is PHYSICAL (0-400 °C for an oven), not the
// specification limits of M7: outside it the sensor is broken (a cut wire reads -32768), so the value is Bad.
public class MachineTag : AuditableEntity, IAudited
{
    public const int TagNameMaxLength = 50;
    public const int UnitMaxLength = 20;

    public int Id { get; private set; }
    public int MachineId { get; private set; }
    public string TagName { get; private set; }          // "peakTemp", the {tag} level of .../process/{tag}
    public string Unit { get; private set; }             // "°C"
    public double LowerPlausible { get; private set; }
    public double UpperPlausible { get; private set; }
    public int? SpcCharacteristicId { get; private set; } // M7: the characteristic this tag feeds (no FK until M7)
    public int? SpcSampleEverySec { get; private set; }   // M7 samples the latest Good value this often
    public bool IsActive { get; private set; } = true;

    private MachineTag() { TagName = Unit = string.Empty; }

    public MachineTag(int machineId, string tagName, string unit, double lowerPlausible, double upperPlausible)
    {
        if (string.IsNullOrWhiteSpace(tagName) || tagName.Length > TagNameMaxLength || tagName.Contains('/'))
            throw new DomainException($"Tag name is required, at most {TagNameMaxLength} characters, without '/'.");
        if (string.IsNullOrWhiteSpace(unit) || unit.Length > UnitMaxLength)
            throw new DomainException($"Unit is required and at most {UnitMaxLength} characters.");
        if (!(lowerPlausible < upperPlausible))
            throw new DomainException("The plausible range needs lower < upper.");
        MachineId = machineId;
        TagName = tagName;
        Unit = unit;
        LowerPlausible = lowerPlausible;
        UpperPlausible = upperPlausible;
    }

    // The quality to store: a value outside the physical range is Bad whatever the source claimed.
    public DataQuality Judge(double value, DataQuality reported)
        => double.IsFinite(value) && value >= LowerPlausible && value <= UpperPlausible ? reported : DataQuality.Bad;

    public void SetActive(bool isActive) => IsActive = isActive;
}

// One reading of a tag. Insert-only, the biggest table if read densely (30 days kept, step 4b), not audited.
// float in SQL Server (8 bytes): enough for a sensor, decimal would only cost space.
public class ProcessValue
{
    public long Id { get; private set; }
    public int MachineTagId { get; private set; }
    public double Value { get; private set; }
    public DataQuality Quality { get; private set; }
    public DateTime SourceTs { get; private set; }       // when the machine / gateway read it, UTC
    public DateTime ReceivedAt { get; private set; }
    public Guid MsgId { get; private set; }              // unique: a duplicated QoS 1 message is stored once

    private ProcessValue() { }

    public ProcessValue(int machineTagId, double value, DataQuality quality, DateTime sourceTs, DateTime receivedAt, Guid msgId)
    {
        if (msgId == Guid.Empty) throw new DomainException("A process value needs its message id.");
        MachineTagId = machineTagId;
        Value = value;
        Quality = quality;
        SourceTs = sourceTs;
        ReceivedAt = receivedAt;
        MsgId = msgId;
    }
}
