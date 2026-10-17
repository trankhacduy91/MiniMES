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
}
