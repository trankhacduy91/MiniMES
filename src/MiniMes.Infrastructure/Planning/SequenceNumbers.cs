using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using MiniMes.Application.Planning;
using MiniMes.Infrastructure.Persistence;
using MiniMes.Infrastructure.Persistence.Configurations;

namespace MiniMes.Infrastructure.Planning;

// NEXT VALUE FOR is SQL Server syntax, so it lives here, not in Application.
public sealed class SequenceNumbers(AppDbContext db) : ISequenceNumbers
{
    // ToListAsync, never SingleAsync/FirstAsync: composing LINQ wraps the SQL in a subquery, where SQL Server
    // refuses NEXT VALUE FOR (found in M2 step 2).
    public async Task<long> NextWorkOrderNumberAsync(CancellationToken ct)
        => (await db.Database
            .SqlQueryRaw<long>($"SELECT NEXT VALUE FOR dbo.{WorkOrderConfiguration.WoNoSequence} AS [Value]")
            .ToListAsync(ct))[0];

    // NEXT VALUE FOR ... OVER (ORDER BY): one number per row of a generated 1..count list, in that order.
    // Allowed here because NEXT VALUE FOR is in the outer SELECT (the derived table only supplies row numbers).
    public async Task<IReadOnlyList<long>> NextSerialNumbersAsync(int count, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        var sql = $"""
            SELECT NEXT VALUE FOR dbo.{WorkOrderConfiguration.SerialSequence} OVER (ORDER BY n.rn) AS [Value]
            FROM (SELECT TOP (@count) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS rn
                  FROM sys.all_objects a CROSS JOIN sys.all_objects b) n
            """;
        return await db.Database.SqlQueryRaw<long>(sql, new SqlParameter("@count", count)).ToListAsync(ct);
    }
}
