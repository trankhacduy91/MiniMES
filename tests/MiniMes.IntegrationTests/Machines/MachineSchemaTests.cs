using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using MiniMes.Domain.Andon;
using MiniMes.Domain.Common;
using MiniMes.Domain.Machines;
using MiniMes.Domain.MasterData.Equipment;
using MiniMes.IntegrationTests.Infrastructure;

namespace MiniMes.IntegrationTests.Machines;

// Spec M4 step 1: the database enforces what ingest, OEE and Andon will rely on. Each test uses its own machine
// so it does not collide with other tests (and later the ingest) on the seeded ones.
[Collection(ApiCollection.Name)]
public class MachineSchemaTests(ApiFixture api)
{
    private static readonly DateTime T0 = new(2026, 10, 17, 1, 0, 0, DateTimeKind.Utc);

    // Spec check of step 1: two open segments of one machine are refused by the database itself.
    [Fact]
    public async Task A_machine_has_at_most_one_open_state_segment()
    {
        var machineId = await NewMachineAsync();
        await SaveAsync(db =>
        {
            var closed = new MachineStateHistory(machineId, MachineState.Running, T0);
            closed.Close(T0.AddMinutes(5));
            db.MachineStateHistory.AddRange(closed, new MachineStateHistory(machineId, MachineState.Down, T0.AddMinutes(5)));
        });

        await Assert.ThrowsAsync<ConflictException>(() => SaveAsync(db =>
            db.MachineStateHistory.Add(new MachineStateHistory(machineId, MachineState.Running, T0.AddMinutes(6)))));

        Assert.Equal(1, await api.WithDbAsync(db => db.MachineStateHistory.CountAsync(h => h.MachineId == machineId && h.EndAt == null)));
    }

    // The auto-raise and an operator's button at the same moment cannot open two calls for one problem.
    [Fact]
    public async Task At_most_one_open_andon_per_machine_and_type()
    {
        var machineId = await NewMachineAsync();
        await SaveAsync(db => db.AndonCalls.AddRange(
            new AndonCall(machineId, AndonType.Machine, AndonCall.SystemUser, T0),
            new AndonCall(machineId, AndonType.Quality, "EMP00123", T0)));          // another type: fine

        await Assert.ThrowsAsync<ConflictException>(() => SaveAsync(db =>
            db.AndonCalls.Add(new AndonCall(machineId, AndonType.Machine, "EMP00123", T0.AddSeconds(5)))));

        // Once the first call is closed (Resolved = 2), the next problem may open a new one.
        await api.WithDbAsync(db => db.Database.ExecuteSqlRawAsync(
            "UPDATE dbo.AndonCalls SET Status = 2 WHERE MachineId = @m AND Type = 0", new SqlParameter("@m", machineId)));
        await SaveAsync(db => db.AndonCalls.Add(new AndonCall(machineId, AndonType.Machine, "EMP00123", T0.AddMinutes(30))));
        Assert.Equal(3, await api.WithDbAsync(db => db.AndonCalls.CountAsync(a => a.MachineId == machineId)));
    }

    // QoS 1 = at least once: the same message stored twice would count production twice.
    [Fact]
    public async Task A_message_is_stored_once()
    {
        var machineId = await NewMachineAsync();
        var tagId = await NewTagAsync(machineId);
        var (eventMsg, valueMsg) = (Guid.NewGuid(), Guid.NewGuid());
        await SaveAsync(db =>
        {
            db.MachineEvents.Add(MachineEvent.StateChanged(eventMsg, machineId, MachineState.Running, T0, T0));
            db.ProcessValues.Add(new ProcessValue(tagId, 245.3, DataQuality.Good, T0, T0, valueMsg));
        });

        await Assert.ThrowsAsync<ConflictException>(() => SaveAsync(db =>
            db.MachineEvents.Add(MachineEvent.StateChanged(eventMsg, machineId, MachineState.Running, T0, T0))));
        await Assert.ThrowsAsync<ConflictException>(() => SaveAsync(db =>
            db.ProcessValues.Add(new ProcessValue(tagId, 245.3, DataQuality.Good, T0, T0, valueMsg))));
    }

    [Fact]
    public async Task Tag_names_are_unique_per_machine_and_ranges_are_checked_by_the_database()
    {
        var machineId = await NewMachineAsync();
        await NewTagAsync(machineId, "peakTemp");

        await Assert.ThrowsAsync<ConflictException>(() => NewTagAsync(machineId, "peakTemp"));
        var ex = await Assert.ThrowsAsync<SqlException>(() => api.WithDbAsync(db => db.Database.ExecuteSqlRawAsync(
            "INSERT INTO dbo.MachineTags (MachineId, TagName, Unit, LowerPlausible, UpperPlausible, IsActive, CreatedAt, CreatedBy) " +
            "VALUES (@m, 'zone1', N'°C', 400, 0, 1, SYSUTCDATETIME(), 'test')", new SqlParameter("@m", machineId))));
        Assert.Contains("CK_MachineTags_Plausible", ex.Message);
    }

    // Enums of the big tables are tinyint (1 byte a row), and the unit keeps its "°".
    [Fact]
    public async Task Column_types_follow_the_spec()
    {
        var types = await api.WithDbAsync(db => db.Database.SqlQueryRaw<string>("""
            SELECT TABLE_NAME + '.' + COLUMN_NAME + '=' + DATA_TYPE AS [Value] FROM INFORMATION_SCHEMA.COLUMNS
            WHERE (TABLE_NAME = 'MachineEvents' AND COLUMN_NAME IN ('Kind', 'State', 'AlarmCode', 'MachineTs'))
               OR (TABLE_NAME = 'AndonCalls' AND COLUMN_NAME IN ('Status', 'RowVersion'))
               OR (TABLE_NAME = 'MachineTags' AND COLUMN_NAME = 'Unit')
               OR (TABLE_NAME = 'ProcessValues' AND COLUMN_NAME = 'Value')
            """).ToListAsync());

        Assert.Equal(
            ["AndonCalls.RowVersion=timestamp", "AndonCalls.Status=tinyint", "MachineEvents.AlarmCode=varchar", "MachineEvents.Kind=tinyint",
             "MachineEvents.MachineTs=datetime2", "MachineEvents.State=tinyint", "MachineTags.Unit=nvarchar", "ProcessValues.Value=float"],
            types.Order());
    }

    // ---------- helpers ----------

    private Task<int> SaveAsync(Action<MiniMes.Infrastructure.Persistence.AppDbContext> change)
        => api.WithDbAsync(db => { change(db); return db.SaveChangesAsync(); });

    private Task<int> NewMachineAsync() => api.WithDbAsync(async db =>
    {
        var code = "T4-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var wc = await db.WorkCenters.Where(w => w.Code == "WC-SMT").Select(w => w.Id).SingleAsync();
        var machine = new Machine(wc, code, "Test machine " + code, "PickAndPlace", code, MachineDataSource.Mqtt);
        db.Machines.Add(machine);
        await db.SaveChangesAsync();
        return machine.Id;
    });

    private Task<int> NewTagAsync(int machineId, string name = "peakTemp") => api.WithDbAsync(async db =>
    {
        var tag = new MachineTag(machineId, name, "°C", 0, 400);
        db.MachineTags.Add(tag);
        await db.SaveChangesAsync();
        return tag.Id;
    });
}
