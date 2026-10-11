using MiniMes.Domain.Common;

namespace MiniMes.Domain.Factory;

// Scans always happen "at a station". Code is unique across the whole system (e.g. L1-SMT-01).
public class Station : AuditableEntity
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
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Code = code;
        Name = name;
        Type = type;
    }
}
