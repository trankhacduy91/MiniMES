using Microsoft.EntityFrameworkCore;
using MiniMes.Application.Execution;
using MiniMes.Infrastructure.Persistence;

namespace MiniMes.Infrastructure.Execution;

// Spec M3 2.10 as plain SQL through EF Core's Database.SqlQuery<T> (unmapped result types, parameters from the
// interpolated string, so no injection). Not Dapper: one data-access library is enough, and EF already has the
// connection. Not LINQ: "first scan of each serial at each step" is a window function (ROW_NUMBER), which reads
// exactly like the spec and like what a DBA would write.
public sealed class ExecutionReportQueries(AppDbContext db) : IExecutionReportQueries
{
    // Seq = 0 is the Repair check-in (off the routing): it must not count as a first pass (OK) anywhere.
    // Per routing step, not per station: two stations can do the same step, and RTY multiplies steps.
    public async Task<IReadOnlyList<FirstPassRow>> FirstPassByStepAsync(int workOrderId, CancellationToken ct)
        => await db.Database.SqlQuery<FirstPassRow>($"""
            WITH FirstAttempt AS (
                SELECT Seq, Result,
                       ROW_NUMBER() OVER (PARTITION BY SerialNo, Seq ORDER BY ScannedAt, Id) AS rn
                FROM   dbo.ScanLogs
                WHERE  WorkOrderId = {workOrderId} AND Seq > 0
            )
            SELECT Seq,
                   COUNT(*)                                       AS Entered,
                   SUM(CASE WHEN Result = 'Ok' THEN 1 ELSE 0 END) AS PassedFirstTime
            FROM   FirstAttempt
            WHERE  rn = 1
            GROUP  BY Seq
            """).ToListAsync(ct);

    // From the Serials table (the current state), not from the log: index (WorkOrderId, Status) INCLUDE (CurrentSeq).
    public async Task<IReadOnlyList<WipCountRow>> WipAsync(int workOrderId, CancellationToken ct)
        => await db.Database.SqlQuery<WipCountRow>($"""
            SELECT CurrentSeq, Status, COUNT(*) AS Qty
            FROM   dbo.Serials
            WHERE  WorkOrderId = {workOrderId} AND Status IN ('InProcess', 'Ng', 'Repair', 'Hold')
            GROUP  BY CurrentSeq, Status
            """).ToListAsync(ct);
}
