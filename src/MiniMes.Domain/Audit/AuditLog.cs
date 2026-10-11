namespace MiniMes.Domain.Audit;

// Business evidence: who changed what, when, old -> new. Insert-only (a DB trigger blocks UPDATE/DELETE).
// Modified: one row per changed column. Added/Deleted: one row, ColumnName = "*", value = JSON of all columns.
public class AuditLog
{
    public const string AllColumns = "*";

    public long Id { get; private set; }
    public string TableName { get; private set; }
    public string RecordKey { get; private set; }
    public string ColumnName { get; private set; }
    public string? OldValue { get; private set; }
    public string? NewValue { get; private set; }
    public string ChangedBy { get; private set; }
    public DateTime ChangedAt { get; private set; } // UTC

    private AuditLog() { TableName = RecordKey = ColumnName = ChangedBy = string.Empty; }

    public AuditLog(string tableName, string recordKey, string columnName,
        string? oldValue, string? newValue, string changedBy, DateTime changedAt)
    {
        TableName = tableName;
        RecordKey = recordKey;
        ColumnName = columnName;
        OldValue = oldValue;
        NewValue = newValue;
        ChangedBy = changedBy;
        ChangedAt = changedAt;
    }
}
