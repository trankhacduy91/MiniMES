using MiniMes.Domain.Common;

namespace MiniMes.Domain.Factory;

public class Line : AuditableEntity, IAudited
{
    public int Id { get; private set; }
    public int AreaId { get; private set; }
    public string Code { get; private set; }
    public string Name { get; private set; }
    public bool IsActive { get; private set; } = true;

    public List<Station> Stations { get; private set; } = [];

    private Line() { Code = Name = string.Empty; }

    internal Line(string code, string name)
    {
        FactoryRules.RequireCode(code);
        FactoryRules.RequireName(name);
        Code = code;
        Name = name;
    }

    public Station AddStation(string code, string name, StationType type)
    {
        FactoryRules.RequireActiveParent(IsActive, "Line", Code);
        var station = new Station(code, name, type);
        Stations.Add(station);
        return station;
    }

    public void Rename(string name)
    {
        FactoryRules.RequireName(name);
        Name = name;
    }

    // No hard delete: deactivate instead so history and audit stay intact.
    public void SetActive(bool isActive) => IsActive = isActive;
}
