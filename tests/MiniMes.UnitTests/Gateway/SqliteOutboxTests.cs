using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MiniMes.EdgeGateway.Queue;

namespace MiniMes.UnitTests.Gateway;

// Spec M4b step 5: the outbox on a real SQLite file (temp folder): order, delete after PUBACK, cap, survives a restart.
public sealed class SqliteOutboxTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "minimes-outbox-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 28, 8, 0, 0, TimeSpan.Zero));
    private SqliteOutbox _outbox = default!;

    private string File => Path.Combine(_dir, "outbox.db");

    public async Task InitializeAsync() => _outbox = await Open(maxRows: 500_000);

    public async Task DisposeAsync()
    {
        await _outbox.DisposeAsync();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* WAL file still mapped: left for the OS */ }
    }

    [Fact]
    public async Task Messages_come_back_in_the_order_they_were_queued()
    {
        await _outbox.EnqueueAsync([("t/a", "1", false), ("t/b", "2", true)]);
        await _outbox.EnqueueAsync([("t/c", "3", false)]);

        var batch = await _outbox.PeekAsync(10);

        Assert.Equal(["1", "2", "3"], batch.Select(m => m.Payload));
        Assert.Equal(("t/b", true), (batch[1].Topic, batch[1].Retain));
        Assert.True(batch[0].Id < batch[1].Id && batch[1].Id < batch[2].Id);
    }

    [Fact]
    public async Task Peek_does_not_remove_only_delete_does()
    {
        await _outbox.EnqueueAsync([("t", "1", false), ("t", "2", false)]);
        var first = (await _outbox.PeekAsync(1)).Single();

        Assert.Equal(2, (await _outbox.PeekAsync(10)).Count);   // not sent yet = still there
        await _outbox.DeleteAsync(first.Id);                    // PUBACK received

        Assert.Equal(["2"], (await _outbox.PeekAsync(10)).Select(m => m.Payload));
    }

    // The spec's test: maxRows 100, 150 written -> 100 left, DroppedCount 50, the 100 NEWEST kept.
    [Fact]
    public async Task Over_max_rows_the_oldest_are_dropped()
    {
        await _outbox.DisposeAsync();
        _outbox = await Open(maxRows: 100);
        await _outbox.EnqueueAsync(Enumerable.Range(0, 150).Select(i => ("t", i.ToString(), false)).ToList());

        var dropped = await _outbox.TrimAsync();

        var left = await _outbox.PeekAsync(1000);
        Assert.Equal(50, dropped);
        Assert.Equal(50, _outbox.DroppedCount);
        Assert.Equal(100, left.Count);
        Assert.Equal(Enumerable.Range(50, 100).Select(i => i.ToString()), left.Select(m => m.Payload));
    }

    [Fact]
    public async Task Older_than_max_age_is_dropped()
    {
        await _outbox.EnqueueAsync([("t", "old", false)]);
        _clock.Advance(TimeSpan.FromDays(7) + TimeSpan.FromMinutes(1));
        await _outbox.EnqueueAsync([("t", "new", false)]);

        await _outbox.TrimAsync();

        Assert.Equal(["new"], (await _outbox.PeekAsync(10)).Select(m => m.Payload));
        Assert.Equal(1, _outbox.DroppedCount);
    }

    [Fact]
    public async Task Under_the_cap_nothing_is_dropped()
    {
        await _outbox.EnqueueAsync([("t", "1", false)]);

        Assert.Equal(0, await _outbox.TrimAsync());
        Assert.Equal(0, _outbox.DroppedCount);
    }

    // The gateway is killed with messages waiting: after the restart they are still there, and new ones go after them.
    [Fact]
    public async Task The_queue_survives_a_restart_and_keeps_its_order()
    {
        await _outbox.EnqueueAsync([("t", "before-1", false), ("t", "before-2", false)]);
        var lastBefore = (await _outbox.PeekAsync(10)).Max(m => m.Id);
        await _outbox.DisposeAsync();

        _outbox = await Open(maxRows: 500_000);
        await _outbox.EnqueueAsync([("t", "after", false)]);

        var all = await _outbox.PeekAsync(10);
        Assert.Equal(["before-1", "before-2", "after"], all.Select(m => m.Payload));
        Assert.True(all[2].Id > lastBefore);
    }

    [Fact]
    public async Task Stats_give_length_and_age_of_the_oldest()
    {
        await _outbox.EnqueueAsync([("t", "1", false)]);
        _clock.Advance(TimeSpan.FromSeconds(42));
        await _outbox.EnqueueAsync([("t", "2", false)]);

        var stats = await _outbox.StatsAsync();

        Assert.Equal(new OutboxStats(2, 0, 42), stats);
        Assert.Equal(new OutboxStats(0, 0, 0), await (await EmptyOutbox()).StatsAsync());
    }

    [Fact]
    public async Task Wait_for_new_returns_as_soon_as_something_is_queued()
    {
        var waiting = _outbox.WaitForNewAsync(TimeSpan.FromHours(1), CancellationToken.None);
        Assert.False(waiting.IsCompleted);

        await _outbox.EnqueueAsync([("t", "1", false)]);

        await waiting.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private Task<SqliteOutbox> Open(long maxRows)
        => SqliteOutbox.OpenAsync(File, maxRows, TimeSpan.FromDays(7), _clock, NullLogger.Instance);

    private async Task<SqliteOutbox> EmptyOutbox()
    {
        await _outbox.DisposeAsync();
        System.IO.File.Delete(File);
        _outbox = await Open(maxRows: 500_000);
        return _outbox;
    }
}
