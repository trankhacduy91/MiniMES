using MiniMes.Domain.Common;

namespace MiniMes.Domain.Factory;

// ISA-95 hierarchy: Site -> Area -> Line -> Station.
// Codes appear in MQTT topics (M4), so keep them stable and space-free.
public class Site : AuditableEntity, IAudited
{
    public int Id { get; private set; }
    public string Code { get; private set; }
    public string Name { get; private set; }
    public bool IsActive { get; private set; } = true;

    public List<Area> Areas { get; private set; } = [];

    private Site() { Code = Name = string.Empty; }

    public Site(string code, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Code = code;
        Name = name;
    }

    public Area AddArea(string code, string name)
    {
        var area = new Area(code, name);
        Areas.Add(area);
        return area;
    }
}
