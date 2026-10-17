using MiniMes.Domain.Common;

namespace MiniMes.Domain.Andon;

public enum AndonType : byte { Machine, Material, Quality, Other }

// Stored as tinyint: the "open" filter of the unique index is Status <= Acknowledged.
public enum AndonStatus : byte { Raised = 0, Acknowledged = 1, Resolved = 2, Cancelled = 3 }

// Spec M4 2.9: a call for help at a machine (TPS jidoka: whoever sees a problem may call, someone must answer in
// time). Raised by an operator or by the system (machine Down too long), acknowledged and resolved by Supervisor /
// Maintenance with a downtime reason (the Pareto of M5). At most one open call per (machine, type): filtered unique
// index. Two people acknowledging at once: RowVersion. The state machine (Acknowledge, Resolve, Cancel, Escalate)
// comes in steps 7 and 7b.
public class AndonCall : IAudited
{
    public const string SystemUser = "system";
    public const int NoteMaxLength = 500;

    public long Id { get; private set; }
    public int MachineId { get; private set; }
    public AndonType Type { get; private set; }
    public AndonStatus Status { get; private set; } = AndonStatus.Raised;
    public string RaisedBy { get; private set; }          // employee code, or "system" for an automatic call
    public DateTime RaisedAt { get; private set; }
    public string? AckBy { get; private set; }
    public DateTime? AckAt { get; private set; }          // AckAt - RaisedAt = reaction time
    public string? ResolvedBy { get; private set; }
    public DateTime? ResolvedAt { get; private set; }     // ResolvedAt - AckAt = repair time
    public int? DowntimeReasonId { get; private set; }
    public string? ResolutionNote { get; private set; }
    public int EscalationLevel { get; private set; }      // [v2] 0 = the first responders; kept for the KPI
    public DateTime? LastEscalatedAt { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    private AndonCall() { RaisedBy = string.Empty; }

    public AndonCall(int machineId, AndonType type, string raisedBy, DateTime nowUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(raisedBy);
        MachineId = machineId;
        Type = type;
        RaisedBy = raisedBy;
        RaisedAt = nowUtc;
    }

    public bool IsOpen => Status is AndonStatus.Raised or AndonStatus.Acknowledged;
}

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
