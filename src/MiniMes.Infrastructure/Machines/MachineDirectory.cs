using System.Collections.Frozen;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MiniMes.Contracts.Machines;
using MiniMes.Infrastructure.Persistence;

namespace MiniMes.Infrastructure.Machines;

public sealed record MachineRef(int Id, string Code, int LineId, string Site, string Area, string Line);

// Topic -> machine, from master data. Cached: a database round trip per message is what the ingest must avoid.
// The whole path must match (site/area/line/MqttTopicKey), not only the last level: a machine moved to another line
// in M1 but still publishing the old topic is a configuration error to see, not data to file under the new line.
// MQTT topics are case-sensitive, so is the lookup. Inactive machines are unknown (IsActive = false, M1).
public sealed class MachineDirectory(IServiceScopeFactory scopes, TimeProvider clock)
{
    // A machine added in M1 shows up without restarting the API; garbage topics cannot hammer the DB with reloads.
    public static readonly TimeSpan MinReloadInterval = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim _reload = new(1, 1);
    private FrozenDictionary<(string Site, string Area, string Line, string Machine), MachineRef> _byTopic = FrozenDictionary<(string, string, string, string), MachineRef>.Empty;
    private DateTimeOffset _loadedAt = DateTimeOffset.MinValue;

    public async ValueTask<MachineRef?> ResolveAsync(ParsedTopic topic, CancellationToken ct)
    {
        var key = (topic.Site, topic.Area, topic.Line, topic.Machine);
        if (_byTopic.TryGetValue(key, out var machine)) return machine;
        if (clock.GetUtcNow() - _loadedAt < MinReloadInterval) return null;

        await _reload.WaitAsync(ct);
        try
        {
            if (clock.GetUtcNow() - _loadedAt >= MinReloadInterval) await LoadAsync(ct);
        }
        finally { _reload.Release(); }
        return _byTopic.GetValueOrDefault(key);
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
        _byTopic = rows.ToFrozenDictionary(r => (r.Ref.Site, r.Ref.Area, r.Ref.Line, r.MqttTopicKey), r => r.Ref);
        _loadedAt = clock.GetUtcNow();
    }
}
