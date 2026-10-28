using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace MiniMes.EdgeGateway.Queue;

public sealed record OutboxMessage(long Id, string Topic, string Payload, bool Retain);

public sealed record OutboxStats(long QueueLength, long DroppedCount, double OldestAgeSec);

// Spec M4b 2.7, rules G4, G5, G11: every message (except heartbeats) is written to disk FIRST; the forwarder reads it
// in Id order, publishes QoS 1 and deletes it only after the broker's PUBACK. Broker away 10 minutes = the table grows;
// gateway restarted = the table is still there. One path for every message, so the order is always the Id order.
// One connection, used by the pipeline (writes) and the forwarder (reads, deletes): SQLite connections are not
// thread-safe, the gate serialises them. WAL lets a reader and a writer work side by side in the file.
public sealed class SqliteOutbox : IAsyncDisposable
{
    private readonly SqliteConnection _db;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeProvider _clock;
    private readonly ILogger _log;
    private readonly long _maxRows;
    private readonly TimeSpan _maxAge;
    private TaskCompletionSource _added = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _dropped;

    private SqliteOutbox(SqliteConnection db, long maxRows, TimeSpan maxAge, TimeProvider clock, ILogger log)
        => (_db, _maxRows, _maxAge, _clock, _log) = (db, maxRows, maxAge, clock, log);

    public string Path => _db.DataSource;

    // Since this process started; the log keeps the history (an Error at every drop).
    public long DroppedCount => Interlocked.Read(ref _dropped);

    public static async Task<SqliteOutbox> OpenAsync(string path, long maxRows, TimeSpan maxAge, TimeProvider clock, ILogger log, CancellationToken ct = default)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        await db.OpenAsync(ct);
        await ExecAsync(db, """
            PRAGMA journal_mode = WAL;      -- a reader and a writer at the same time
            PRAGMA synchronous = NORMAL;    -- with WAL: safe against a process crash, a power cut may lose the last commits
            CREATE TABLE IF NOT EXISTS Outbox (
              Id        INTEGER PRIMARY KEY AUTOINCREMENT,   -- the sending order (AUTOINCREMENT: never reused)
              Topic     TEXT    NOT NULL,
              Payload   TEXT    NOT NULL,                    -- the JSON with its msgId: a resend is byte-identical
              Retain    INTEGER NOT NULL,
              CreatedAt TEXT    NOT NULL                     -- UTC, ISO 8601: sorts as text
            );
            """, ct);
        return new SqliteOutbox(db, maxRows, maxAge, clock, log);
    }

    // One transaction for the whole batch: one fsync instead of one per message (spec 2.7, trap 3).
    public async Task EnqueueAsync(IReadOnlyList<(string Topic, string Payload, bool Retain)> messages, CancellationToken ct = default)
    {
        if (messages.Count == 0) return;
        await _gate.WaitAsync(ct);
        try
        {
            await using var tx = (SqliteTransaction)await _db.BeginTransactionAsync(ct);
            await using var cmd = _db.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO Outbox (Topic, Payload, Retain, CreatedAt) VALUES ($topic, $payload, $retain, $at)";
            var topic = cmd.Parameters.Add("$topic", SqliteType.Text);
            var payload = cmd.Parameters.Add("$payload", SqliteType.Text);
            var retain = cmd.Parameters.Add("$retain", SqliteType.Integer);
            cmd.Parameters.AddWithValue("$at", Now());
            foreach (var m in messages)
            {
                (topic.Value, payload.Value, retain.Value) = (m.Topic, m.Payload, m.Retain ? 1 : 0);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
        }
        finally { _gate.Release(); }
        Interlocked.Exchange(ref _added, new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
    }

    public async Task<IReadOnlyList<OutboxMessage>> PeekAsync(int take, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT Id, Topic, Payload, Retain FROM Outbox ORDER BY Id LIMIT $take";
            cmd.Parameters.AddWithValue("$take", take);
            var rows = new List<OutboxMessage>();
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) rows.Add(new(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetInt64(3) != 0));
            return rows;
        }
        finally { _gate.Release(); }
    }

    // Only after the PUBACK of this message (rule G4).
    public async Task DeleteAsync(long id, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await using var cmd = _db.CreateCommand();
            cmd.CommandText = "DELETE FROM Outbox WHERE Id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally { _gate.Release(); }
    }

    // Returns when something is enqueued, or after the timeout: no busy loop while the queue is empty. A message
    // enqueued just before the call is picked up at the latest after the timeout.
    public async Task WaitForNewAsync(TimeSpan timeout, CancellationToken ct)
    {
        try { await Volatile.Read(ref _added).Task.WaitAsync(timeout, _clock, ct); }
        catch (TimeoutException) { }
    }

    // Rule G11: the disk is not infinite. Over maxRows, or older than maxAge: the OLDEST go, with an Error in the log.
    // Counters are cumulative, so the total production survives; only the detail of the dropped period is lost.
    public async Task<int> TrimAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        int dropped;
        try
        {
            await using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                DELETE FROM Outbox WHERE Id IN (
                  SELECT Id FROM Outbox ORDER BY Id
                  LIMIT max((SELECT COUNT(*) FROM Outbox) - $maxRows, 0));
                DELETE FROM Outbox WHERE CreatedAt < $oldest;
                """;
            cmd.Parameters.AddWithValue("$maxRows", _maxRows);
            cmd.Parameters.AddWithValue("$oldest", Format(_clock.GetUtcNow().UtcDateTime - _maxAge));
            dropped = await cmd.ExecuteNonQueryAsync(ct);
        }
        finally { _gate.Release(); }

        if (dropped > 0)
        {
            Interlocked.Add(ref _dropped, dropped);
            _log.LogError("Outbox full or too old: dropped the {Dropped} oldest messages (max {MaxRows} rows, {MaxAgeDays} days); {Total} dropped since start",
                dropped, _maxRows, _maxAge.TotalDays, DroppedCount);
        }
        return dropped;
    }

    public async Task<OutboxStats> StatsAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*), MIN(CreatedAt) FROM Outbox";
            await using var r = await cmd.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            var length = r.GetInt64(0);
            var oldest = r.IsDBNull(1) ? (DateTime?)null : DateTime.Parse(r.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
            var age = oldest is null ? 0 : Math.Max(0, (_clock.GetUtcNow().UtcDateTime - oldest.Value).TotalSeconds);
            return new OutboxStats(length, DroppedCount, Math.Round(age, 1));
        }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await _db.DisposeAsync();
        _gate.Dispose();
    }

    private string Now() => Format(_clock.GetUtcNow().UtcDateTime);

    private static string Format(DateTime utc) => utc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static async Task ExecAsync(SqliteConnection db, string sql, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
