namespace MiniMes.Contracts.Audit;

public sealed record AuditLogDto(
    long Id,
    string TableName,
    string RecordKey,
    string ColumnName,
    string? OldValue,
    string? NewValue,
    string ChangedBy,
    DateTime ChangedAt);

// Query string of GET /api/audit-logs. From/To are UTC, To is exclusive.
public sealed class AuditLogFilter
{
    public const int MaxPageSize = 200;

    public string? TableName { get; init; }
    public string? RecordKey { get; init; }
    public DateTime? From { get; init; }
    public DateTime? To { get; init; }
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 50;
}
