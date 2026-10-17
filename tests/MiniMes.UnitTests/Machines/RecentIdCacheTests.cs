using Microsoft.Extensions.Time.Testing;
using MiniMes.Application.Machines;

namespace MiniMes.UnitTests.Machines;

// Spec M4 step 3: the in-RAM layer against QoS 1 redeliveries.
public class RecentIdCacheTests
{
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 17, 1, 0, 0, TimeSpan.Zero));

    [Fact]
    public void Same_id_is_new_once_then_a_duplicate()
    {
        var cache = new RecentIdCache(_clock);
        var id = Guid.NewGuid();

        Assert.True(cache.TryAdd(id));
        _clock.Advance(TimeSpan.FromMinutes(9));
        Assert.False(cache.TryAdd(id));
        Assert.True(cache.TryAdd(Guid.NewGuid()));   // another id is not affected
    }

    // Past the TTL the id is forgotten: memory is bounded, the unique index in the DB is the last net.
    [Fact]
    public void Ids_older_than_the_ttl_are_forgotten_and_purged()
    {
        var cache = new RecentIdCache(_clock, TimeSpan.FromMinutes(10));
        var old = Guid.NewGuid();
        cache.TryAdd(old);
        foreach (var _ in Enumerable.Range(0, 99)) cache.TryAdd(Guid.NewGuid());
        Assert.Equal(100, cache.Count);

        _clock.Advance(TimeSpan.FromMinutes(10));
        Assert.True(cache.TryAdd(Guid.NewGuid()));   // triggers the purge

        Assert.Equal(1, cache.Count);
        Assert.True(cache.TryAdd(old));
    }

    // The ingest handler is sequential today, but the cache must not let two threads both "win" the same id.
    [Fact]
    public async Task Concurrent_adds_of_one_id_let_exactly_one_through()
    {
        var cache = new RecentIdCache(_clock);
        var id = Guid.NewGuid();

        var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() => cache.TryAdd(id))));

        Assert.Single(results, r => r);
    }
}
