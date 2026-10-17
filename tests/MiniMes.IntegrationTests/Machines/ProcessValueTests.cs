using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MiniMes.Application.Machines;
using MiniMes.Contracts.Machines;
using MiniMes.Domain.Machines;
using MiniMes.Domain.MasterData.Equipment;
using MiniMes.Infrastructure.Machines;
using MiniMes.IntegrationTests.Infrastructure;
using DataQuality = MiniMes.Domain.Machines.DataQuality;

namespace MiniMes.IntegrationTests.Machines;

// [v2] Spec M4 step 4b: process tags from master data, through the store and the batch writer into ProcessValues.
[Collection(ApiCollection.Name)]
public class ProcessValueTests(ApiFixture api)
{
    private static readonly DateTime T0 = new(2026, 10, 17, 3, 0, 0, DateTimeKind.Utc);

    private IServiceScopeFactory Scopes => api.Factory.Services.GetRequiredService<IServiceScopeFactory>();
    private MachineDirectory Directory => api.Factory.Services.GetRequiredService<MachineDirectory>();

    [Fact]
    public async Task Seeded_tag_resolves_and_unknown_tags_do_not()
    {
        var reflow = await api.WithDbAsync(db => db.Machines.Where(m => m.Code == "L1-REFLOW-01").Select(m => m.Id).SingleAsync());

        var tag = await Directory.ResolveTagAsync(reflow, "peakTemp", default);

        Assert.Equal(("°C", 0d, 400d), (tag!.Unit, tag.LowerPlausible, tag.UpperPlausible));
        Assert.Null(await Directory.ResolveTagAsync(reflow, "zone9Temp", default));
        Assert.Null(await Directory.ResolveTagAsync(reflow, "peaktemp", default));   // case-sensitive like the topic
    }

    // Spec check: 10 values -> 10 rows; -32768 -> Bad; the same msgId twice -> 1 row.
    [Fact]
    public async Task Readings_are_stored_with_their_quality_and_once_per_msg_id()
    {
        var tag = await NewTagAsync();
        var store = new MachineRuntimeStore();
        var items = Enumerable.Range(0, 10)
            .Select(i => store.ApplyProcess(tag, Reading(240 + i * 0.5, T0.AddSeconds(i)), T0.AddSeconds(i)).Item!)
            .ToList();
        var broken = store.ApplyProcess(tag, Reading(-32768, T0.AddSeconds(20)), T0.AddSeconds(20)).Item!;

        await WriteAsync([.. items, broken]);
        await WriteAsync(broken);                                     // redelivered after a restart (empty RAM cache)

        var rows = await api.WithDbAsync(db => db.ProcessValues.Where(v => v.MachineTagId == tag.Id).OrderBy(v => v.SourceTs).ToListAsync());
        Assert.Equal(11, rows.Count);
        Assert.All(rows.Take(10), r => Assert.Equal(DataQuality.Good, r.Quality));
        Assert.Equal((-32768d, DataQuality.Bad), (rows[10].Value, rows[10].Quality));
    }

    // 30 days kept, deleted in chunks of 5,000: 12,000 old rows take 3 statements, newer rows stay.
    [Fact]
    public async Task Retention_deletes_rows_older_than_30_days_in_chunks()
    {
        var tag = await NewTagAsync();
        var old = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        await api.WithDbAsync(db => db.Database.ExecuteSqlRawAsync("""
            INSERT INTO dbo.ProcessValues (MachineTagId, Value, Quality, SourceTs, ReceivedAt, MsgId)
            SELECT TOP (12000) @tag, 1, 0, @old, @old, NEWID() FROM sys.all_objects a CROSS JOIN sys.all_objects b;
            INSERT INTO dbo.ProcessValues (MachineTagId, Value, Quality, SourceTs, ReceivedAt, MsgId)
            VALUES (@tag, 2, 0, @recent, @recent, NEWID());
            """, new SqlParameter("@tag", tag.Id), new SqlParameter("@old", old), new SqlParameter("@recent", old.AddDays(15))));
        // "Now" is 30 days and 1 hour after the old rows: only they are past the cutoff, other tests' rows are newer.
        var clock = new FakeTimeProvider(new DateTimeOffset(old.AddDays(30).AddHours(1)));
        var job = new ProcessValueRetentionJob(Scopes, clock, NullLogger<ProcessValueRetentionJob>.Instance);

        var deleted = await job.DeleteExpiredAsync(default);

        Assert.Equal(12000, deleted);
        Assert.Equal([2d], await api.WithDbAsync(db => db.ProcessValues.Where(v => v.MachineTagId == tag.Id).Select(v => v.Value).ToListAsync()));
    }

    // ---------- helpers ----------

    private static MachineMessage Reading(double value, DateTime ts)
        => new(Guid.NewGuid(), "T", new DateTimeOffset(ts), Tag: "peakTemp", Value: value, Source: MachineSources.OpcUa);

    private async Task WriteAsync(params IngestItem[] items)
    {
        var channel = MachineEventBatchWriter.CreateChannel();
        foreach (var item in items) await channel.Writer.WriteAsync(item);
        var writer = new MachineEventBatchWriter(channel, Scopes, TimeProvider.System, NullLogger<MachineEventBatchWriter>.Instance);
        await writer.StartAsync(default);
        await writer.StopAsync(default);
    }

    private Task<MachineTag> NewTagAsync() => api.WithDbAsync(async db =>
    {
        var code = "T4-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var wc = await db.WorkCenters.Where(w => w.Code == "WC-SMT").Select(w => w.Id).SingleAsync();
        var machine = new Machine(wc, code, "Test oven " + code, "ReflowOven", code, MachineDataSource.OpcUa);
        db.Machines.Add(machine);
        await db.SaveChangesAsync();
        var tag = new MachineTag(machine.Id, "peakTemp", "°C", 0, 400);
        db.MachineTags.Add(tag);
        await db.SaveChangesAsync();
        return tag;
    });
}
