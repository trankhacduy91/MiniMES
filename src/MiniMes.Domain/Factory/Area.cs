using MiniMes.Domain.Common;

namespace MiniMes.Domain.Factory;

public class Area : AuditableEntity, IAudited
{
    public int Id { get; private set; }
    public int SiteId { get; private set; }
    public string Code { get; private set; }
    public string Name { get; private set; }
    public bool IsActive { get; private set; } = true;

    public List<Line> Lines { get; private set; } = [];

    private Area() { Code = Name = string.Empty; }

    internal Area(string code, string name)
    {
        FactoryRules.RequireCode(code);
        FactoryRules.RequireName(name);
        Code = code;
        Name = name;
    }

    public Line AddLine(string code, string name)
    {
        FactoryRules.RequireActiveParent(IsActive, "Area", Code);
        var line = new Line(code, name);
        Lines.Add(line);
        return line;
    }

    public void Rename(string name)
    {
        FactoryRules.RequireName(name);
        Name = name;
    }

    // No hard delete: deactivate instead so history and audit stay intact.
    public void SetActive(bool isActive) => IsActive = isActive;
}
