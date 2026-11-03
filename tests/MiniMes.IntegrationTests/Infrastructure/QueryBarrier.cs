using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace MiniMes.IntegrationTests.Infrastructure;

// Test hook to force a check-then-insert race: the chosen query of N callers waits until all N have run it, so
// every caller sees the same "nothing there yet" before any of them inserts. Disarms itself once released.
public sealed class QueryBarrier : DbCommandInterceptor
{
    private volatile Barrier? _armed;

    private sealed class Barrier(Func<DbCommand, bool> match, int parties)
    {
        private int _arrived;
        public Func<DbCommand, bool> Match { get; } = match;
        public TaskCompletionSource AllArrived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Arrive() => Interlocked.Increment(ref _arrived) == parties;
    }

    public void Hold(Func<DbCommand, bool> match, int parties) => _armed = new Barrier(match, parties);

    // False after the test = the callers never met at the barrier: the race was not forced, the test proves nothing.
    public bool WasReleased => _armed is null;

    // The last SpcAlerts command seen, for the failure message when the barrier did not match (wrong text/params).
    public string? LastSeen { get; private set; }

    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
        InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        if (command.CommandText.Contains("SpcAlerts")) LastSeen = command.CommandText + " | " +
            string.Join(",", command.Parameters.Cast<DbParameter>().Select(p => $"{p.ParameterName}={p.Value}({p.Value?.GetType().Name})"));
        if (_armed is { } barrier && barrier.Match(command))
        {
            if (barrier.Arrive())
            {
                _armed = null;
                barrier.AllArrived.TrySetResult();
            }
            // A caller that never arrives must not hang the suite: the test then fails on its assertions instead.
            await barrier.AllArrived.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        }
        return result;
    }

    public static bool HasParameter(DbCommand command, object value)
        => command.Parameters.Cast<DbParameter>().Any(p => Equals(p.Value, value));
}
