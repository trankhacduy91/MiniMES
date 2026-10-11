namespace MiniMes.Domain.Common;

// Filled by AuditSaveChangesInterceptor when the entity is Added; code never sets these by hand.
public abstract class AuditableEntity
{
    public DateTime CreatedAt { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
}
