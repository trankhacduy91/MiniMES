namespace MiniMes.Domain.Common;

// Filled by AuditSaveChangesInterceptor from step 6; the seeder sets them until then.
public abstract class AuditableEntity
{
    public DateTime CreatedAt { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
}
