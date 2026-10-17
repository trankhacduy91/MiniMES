using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace MiniMes.IntegrationTests.Infrastructure;

// Test hook to replay a race in a fixed order: the scan of ONE chosen serial stops at its "last scan" query
// (after it has read the work order's status) until the test opens the gate. Everything else runs normally.
public sealed class ScanGate : DbCommandInterceptor
{
    private volatile Hold? _hold;

    public sealed class Hold(string serialNo)
    {
        public string SerialNo { get; } = serialNo;
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Released { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public Hold HoldScanOf(string serialNo) => _hold = new Hold(serialNo);

    public void Open()
    {
        var hold = _hold;
        _hold = null;
        hold?.Released.TrySetResult();
    }

    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
        InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        // ScanContextReader: ... FROM [ScanLogs] ... ORDER BY [s].[ScannedAt] DESC, parameter = the serial number.
        if (_hold is { } hold && command.CommandText.Contains("[ScannedAt] DESC")
            && command.Parameters.Cast<DbParameter>().Any(p => Equals(p.Value, hold.SerialNo)))
        {
            hold.Reached.TrySetResult();
            await hold.Released.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
        }
        return result;
    }
}
