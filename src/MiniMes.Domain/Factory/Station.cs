using MiniMes.Domain.Common;

namespace MiniMes.Domain.Factory;

// Scans always happen "at a station". Code is unique across the whole system (e.g. L1-SMT-01).
public class Station : AuditableEntity, IAudited
{
    public int Id { get; private set; }
    public int LineId { get; private set; }
    public string Code { get; private set; }
    public string Name { get; private set; }
    public StationType Type { get; private set; }
    public int? WorkCenterId { get; private set; }   // which routing operation this station does (M3 route check); null = Repair, off-routing
    public bool IsActive { get; private set; } = true;

    private Station() { Code = Name = string.Empty; }

    internal Station(int lineId, string code, string name, StationType type)
    {
        FactoryRules.RequireCode(code);
        FactoryRules.RequireName(name);
        LineId = lineId;
        Code = code;
        Name = name;
        Type = type;
    }

    public void Rename(string name)
    {
        FactoryRules.RequireName(name);
        Name = name;
    }

    public void ChangeType(StationType type) => Type = type;

    // The work center must be on the station's own line: a Line 1 station cannot do Line 2's SMT step.
    public void AssignWorkCenter(int? workCenterId, int? workCenterLineId)
    {
        if (workCenterId is not null && workCenterLineId != LineId)
            throw new DomainException($"Work center {workCenterId} is on another line than station '{Code}'.");
        WorkCenterId = workCenterId;
    }

    // No hard delete: deactivate instead so history and audit stay intact.
    public void SetActive(bool isActive) => IsActive = isActive;
}
