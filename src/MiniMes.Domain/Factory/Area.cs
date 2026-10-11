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
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Code = code;
        Name = name;
    }

    public Line AddLine(string code, string name)
    {
        var line = new Line(code, name);
        Lines.Add(line);
        return line;
    }
}
