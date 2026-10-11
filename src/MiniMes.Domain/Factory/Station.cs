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
    public bool IsActive { get; private set; } = true;

    private Station() { Code = Name = string.Empty; }

    internal Station(string code, string name, StationType type)
    {
        FactoryRules.RequireCode(code);
        FactoryRules.RequireName(name);
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

    // No hard delete: deactivate instead so history and audit stay intact.
    public void SetActive(bool isActive) => IsActive = isActive;
}
