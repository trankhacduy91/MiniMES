using System.Collections.Frozen;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MiniMes.Contracts.Machines;
using MiniMes.Domain.Machines;
using MiniMes.Infrastructure.Persistence;

namespace MiniMes.Infrastructure.Machines;

public sealed record MachineRef(int Id, string Code, int LineId, string Site, string Area, string Line);

// Topic -> machine, from master data. Cached: a database round trip per message is what the ingest must avoid.
// The whole path must match (site/area/line/MqttTopicKey), not only the last level: a machine moved to another line
// in M1 but still publishing the old topic is a configuration error to see, not data to file under the new line.
// MQTT topics are case-sensitive, so is the lookup. Inactive machines are unknown (IsActive = false, M1).
// [v2] Same for process tags (step 4b): only active MachineTag rows are accepted; the cached entities are read-only.
public sealed class MachineDirectory(IServiceScopeFactory scopes, TimeProvider clock)
{
    // A machine added in M1 shows up without restarting the API; garbage topics cannot hammer the DB with reloads.
    public static readonly TimeSpan MinReloadInterval = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim _reload = new(1, 1);
    private FrozenDictionary<(string Site, string Area, string Line, string Machine), MachineRef> _byTopic = FrozenDictionary<(string, string, string, string), MachineRef>.Empty;
    private FrozenDictionary<int, MachineRef> _byId = FrozenDictionary<int, MachineRef>.Empty;
    private FrozenDictionary<(int MachineId, string Tag), MachineTag> _tags = FrozenDictionary<(int, string), MachineTag>.Empty;
    private DateTimeOffset _loadedAt = DateTimeOffset.MinValue;

    public async ValueTask<MachineRef?> ResolveAsync(ParsedTopic topic, CancellationToken ct)
    {
        var key = (topic.Site, topic.Area, topic.Line, topic.Machine);
        if (_byTopic.TryGetValue(key, out var machine)) return machine;
        return await ReloadIfDueAsync(ct) ? _byTopic.GetValueOrDefault(key) : null;
    }

    // A machine already seen by the ingest (the cache is loaded on the first message), for logs and pushes.
    public MachineRef? Get(int machineId) => _byId.GetValueOrDefault(machineId);

    public string CodeOf(int machineId) => Get(machineId)?.Code ?? $"machine #{machineId}";

    public async ValueTask<MachineTag?> ResolveTagAsync(int machineId, string tagName, CancellationToken ct)
    {
        var key = (machineId, tagName);
        if (_tags.TryGetValue(key, out var tag)) return tag;
        return await ReloadIfDueAsync(ct) ? _tags.GetValueOrDefault(key) : null;
    }

    private async ValueTask<bool> ReloadIfDueAsync(CancellationToken ct)
    {
        if (clock.GetUtcNow() - _loadedAt < MinReloadInterval) return false;
        await _reload.WaitAsync(ct);
        try
        {
            if (clock.GetUtcNow() - _loadedAt >= MinReloadInterval) await LoadAsync(ct);
        }
        finally { _reload.Release(); }
        return true;
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var rows = await (
            from m in db.Machines.AsNoTracking()
            where m.IsActive
            join wc in db.WorkCenters on m.WorkCenterId equals wc.Id
            join l in db.Lines on wc.LineId equals l.Id
            join a in db.Areas on l.AreaId equals a.Id
            join s in db.Sites on a.SiteId equals s.Id
            select new { m.MqttTopicKey, Ref = new MachineRef(m.Id, m.Code, l.Id, s.Code, a.Code, l.Code) })
            .ToListAsync(ct);
        var tags = await db.MachineTags.AsNoTracking().Where(t => t.IsActive).ToListAsync(ct);
        _byTopic = rows.ToFrozenDictionary(r => (r.Ref.Site, r.Ref.Area, r.Ref.Line, r.MqttTopicKey), r => r.Ref);
        _tags = tags.ToFrozenDictionary(t => (t.MachineId, t.TagName), t => t);
        _byId = rows.ToFrozenDictionary(r => r.Ref.Id, r => r.Ref);
        _loadedAt = clock.GetUtcNow();
    }
}
