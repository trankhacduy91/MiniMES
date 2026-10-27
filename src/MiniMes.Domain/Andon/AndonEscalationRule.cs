using MiniMes.Domain.Common;

namespace MiniMes.Domain.Andon;

// [v2] Spec M4 2.9b: who is told when a call stays Raised too long. Level 0 = at raise; each level once.
public class AndonEscalationRule : AuditableEntity, IAudited
{
    public const int NotifyRolesMaxLength = 100;
    public const int NoteMaxLength = 200;

    public AndonType AndonType { get; private set; }
    public int Level { get; private set; }
    public int AfterMinutes { get; private set; }         // counted from RaisedAt
    public string NotifyRoles { get; private set; }       // role names, comma separated: "Supervisor,Maintenance"
    public string? Note { get; private set; }

    private AndonEscalationRule() { NotifyRoles = string.Empty; }

    public AndonEscalationRule(AndonType andonType, int level, int afterMinutes, string notifyRoles, string? note = null)
    {
        if (level < 0) throw new DomainException("Escalation level cannot be negative.");
        if (afterMinutes < 0) throw new DomainException("AfterMinutes cannot be negative.");
        if (string.IsNullOrWhiteSpace(notifyRoles) || notifyRoles.Length > NotifyRolesMaxLength)
            throw new DomainException($"NotifyRoles is required and at most {NotifyRolesMaxLength} characters.");
        AndonType = andonType;
        Level = level;
        AfterMinutes = afterMinutes;
        NotifyRoles = notifyRoles;
        Note = note;
    }

    public IReadOnlyList<string> Roles => NotifyRoles.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

// [v2] Spec M4 2.9b: the level a call deserves after waiting `elapsed` since RaisedAt = the highest level of its type
// whose AfterMinutes has passed (0 when none). Pure, so "3 minutes later" is tested without a clock or a database.
public static class AndonEscalation
{
    public static int DueLevel(IEnumerable<AndonEscalationRule> rules, AndonType type, TimeSpan elapsed)
        => rules.Where(r => r.AndonType == type && TimeSpan.FromMinutes(r.AfterMinutes) <= elapsed)
            .Select(r => r.Level)
            .DefaultIfEmpty(0)
            .Max();
}
