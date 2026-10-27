using MiniMes.Domain.Common;

namespace MiniMes.Domain.Andon;

public enum AndonType : byte { Machine, Material, Quality, Other }

// Stored as tinyint: the "open" filter of the unique index is Status <= Acknowledged.
public enum AndonStatus : byte { Raised = 0, Acknowledged = 1, Resolved = 2, Cancelled = 3 }

// Spec M4 2.9: a call for help at a machine (TPS jidoka: whoever sees a problem may call, someone must answer in
// time). Raised by an operator or by the system (machine Down too long), acknowledged and resolved by Supervisor /
// Maintenance with a downtime reason (the Pareto of M5). At most one open call per (machine, type): filtered unique
// index. Two people acknowledging at once: RowVersion.
//   Raised -> Acknowledged -> Resolved      (normal path, Resolve needs a downtime reason)
//   Raised -> Resolved                      (fixed at once: AckBy/AckAt filled so reaction time is never null)
//   Raised | Acknowledged -> Cancelled      (raised by mistake; no reason, kept out of the Pareto)
// The machine running again does NOT close the call: nobody has said yet why it stopped. Escalate comes in step 7b.
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

    public void Acknowledge(string by, DateTime nowUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(by);
        if (Status != AndonStatus.Raised)
            throw new DomainException($"Andon {Id} is {Status}; only a Raised call can be acknowledged.");
        Status = AndonStatus.Acknowledged;
        AckBy = by;
        AckAt = nowUtc;
    }

    public void Resolve(string by, int downtimeReasonId, string? note, DateTime nowUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(by);
        RequireOpen("resolved");
        if (downtimeReasonId <= 0) throw new DomainException("A downtime reason is required to resolve an Andon.");
        if (note?.Length > NoteMaxLength) throw new DomainException($"Resolution note is at most {NoteMaxLength} characters.");
        if (Status == AndonStatus.Raised) { AckBy = by; AckAt = nowUtc; }   // fixed straight away, no separate ack
        Status = AndonStatus.Resolved;
        ResolvedBy = by;
        ResolvedAt = nowUtc;
        DowntimeReasonId = downtimeReasonId;
        ResolutionNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
    }

    public void Cancel(string by, DateTime nowUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(by);
        RequireOpen("cancelled");
        Status = AndonStatus.Cancelled;
        ResolvedBy = by;
        ResolvedAt = nowUtc;
    }

    private void RequireOpen(string action)
    {
        if (!IsOpen) throw new DomainException($"Andon {Id} is already {Status}; it cannot be {action}.");
    }
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
