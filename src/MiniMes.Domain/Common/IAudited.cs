namespace MiniMes.Domain.Common;

// Marker: changes to entities with this interface are written to AuditLog by the interceptor.
// Master data and WorkOrder only; big insert-only logs (ScanLog, MachineEvent) are not audited.
public interface IAudited;

// The change is still recorded, but the value is masked (e.g. password hashes).
[AttributeUsage(AttributeTargets.Property)]
public sealed class AuditIgnoreAttribute : Attribute;
