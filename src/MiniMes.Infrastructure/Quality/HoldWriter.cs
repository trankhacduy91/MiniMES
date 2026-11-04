using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MiniMes.Application.Quality.Holds;
using MiniMes.Infrastructure.Persistence;

namespace MiniMes.Infrastructure.Quality;

// Spec M7 2.6: INSERT ... SELECT, one HoldRecord per target in one statement each (20,000 boards of one lot are two
// round trips, not 20,000 rows through the change tracker). The Ids come as one JSON parameter (OPENJSON); every value
// is a parameter (ExecuteSqlInterpolated), never concatenated. The joins re-check the status at write time:
// a serial scrapped (Q10) or a lot consumed since the selection is not held.
public sealed class HoldWriter(AppDbContext db) : IHoldWriter
{
    public async Task<(int Serials, int Lots)> InsertAsync(HoldBatch b, IReadOnlyCollection<long> serialIds, IReadOnlyCollection<int> lotIds,
        CancellationToken ct)
    {
        var scope = b.Scope.ToString();
        var serials = 0;
        if (serialIds.Count > 0)
        {
            var ids = JsonSerializer.Serialize(serialIds);
            serials = await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT dbo.HoldRecords (TargetType, TargetId, BatchId, Scope, ScopeRef, Reason, NcrId, HeldBy, HeldAt)
                SELECT 'Serial', s.Id, {b.Id}, {scope}, {b.ScopeRef}, {b.Reason}, {b.NcrId}, {b.By}, {b.At}
                FROM OPENJSON({ids}) j
                JOIN dbo.Serials s ON s.Id = CAST(j.[value] AS bigint)
                WHERE s.Status <> 'Scrapped'
                """, ct);
        }
        var lots = 0;
        if (lotIds.Count > 0)
        {
            var ids = JsonSerializer.Serialize(lotIds);
            // MaterialLotStatus is stored as a number: 0 Available, 1 Hold.
            lots = await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT dbo.HoldRecords (TargetType, TargetId, BatchId, Scope, ScopeRef, Reason, NcrId, HeldBy, HeldAt)
                SELECT 'Lot', l.Id, {b.Id}, {scope}, {b.ScopeRef}, {b.Reason}, {b.NcrId}, {b.By}, {b.At}
                FROM OPENJSON({ids}) j
                JOIN dbo.MaterialLots l ON l.Id = CAST(j.[value] AS int)
                WHERE l.Status IN (0, 1)
                """, ct);
        }
        return (serials, lots);
    }
}
