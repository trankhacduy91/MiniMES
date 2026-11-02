using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using MiniMes.Application.Traceability;
using MiniMes.Domain.Material;
using MiniMes.Infrastructure.Persistence;
using MiniMes.Infrastructure.Persistence.Seed;
using MiniMes.Infrastructure.Traceability;
using MiniMes.IntegrationTests.Infrastructure;

namespace MiniMes.IntegrationTests.Traceability;

// Spec M6 step 7: the recursive trace queries against the test oracle of section 3 (TraceabilitySeeder):
// forward L-CAP-A = 120, L-CAP-B = 80, L-CAP-P = 80 (through genealogy), SUP-S1 + CAP-100NF = 200, L-RES-A = 200;
// backward serial 150 = {L-CAP-B (0), L-CAP-P (1), L-RES-A (0)}, serial 50 = {L-CAP-A, L-RES-A}.
// One scenario for the class (seeded once, insert-only data); the cycle test seeds its own.
[Collection(ApiCollection.Name)]
public class TraceQueriesTests(ApiFixture api)
{
    private static TraceabilitySeeder.Scenario? _scenario;

    private async Task<TraceabilitySeeder.Scenario> ScenarioAsync(string? tag = null)
    {
        if (tag is null && _scenario is not null) return _scenario;
        using var scope = api.Factory.Services.CreateScope();
        var s = await scope.ServiceProvider.GetRequiredService<TraceabilitySeeder>().SeedAsync(tag ?? ApiFixture.Unique("Q")[2..]);
        if (tag is null) _scenario = s;
        return s;
    }

    private async Task<T> TraceAsync<T>(Func<ITraceQueries, Task<T>> query)
    {
        using var scope = api.Factory.Services.CreateScope();
        return await query(scope.ServiceProvider.GetRequiredService<ITraceQueries>());
    }

    private Task<int> CapItemIdAsync() => api.WithDbAsync(db => db.Items.Where(i => i.Code == "CAP-100NF").Select(i => i.Id).SingleAsync());

    public static TheoryData<string, int, int, int, int> ForwardByLot() => new()
    {
        // lot, serials, first board, last board, depth of every row
        { "L-CAP-A", 120, 1, 120, 0 },
        { "L-CAP-B", 80, 121, 200, 0 },
        { "L-CAP-P", 80, 121, 200, 1 },   // never on a machine: reached through LotGenealogy only
        { "L-RES-A", 200, 1, 200, 0 },
    };

    [Theory]
    [MemberData(nameof(ForwardByLot))]
    public async Task Forward_from_a_lot_matches_the_oracle(string lot, int count, int first, int last, int depth)
    {
        var s = await ScenarioAsync();

        var rows = await TraceAsync(q => q.ForwardByLotAsync(s.Code(lot), default));

        Assert.Equal(count, rows.Count);
        Assert.Equal(count, rows.Select(r => r.SerialNo).Distinct().Count());   // no serial twice
        Assert.Equal(Enumerable.Range(first, last - first + 1).Select(s.SerialNo), rows.Select(r => r.SerialNo));
        Assert.All(rows, r => Assert.Equal(depth, r.Depth));
        Assert.All(rows, r => Assert.Equal(("CompletedUnpacked", "Completed", s.Code("WO-TRACE")), (r.Location, r.SerialStatus, r.WoNo)));
    }

    // Both reels of supplier lot SUP-S1 (L-CAP-A, L-CAP-P) and the child L-CAP-B: every board once, at its closest lot.
    [Fact]
    public async Task Forward_from_a_supplier_lot_reaches_all_200_boards_once()
    {
        var s = await ScenarioAsync();

        var rows = await TraceAsync(async q => await q.ForwardBySupplierLotAsync(s.Code("SUP-S1"), await CapItemIdAsync(), default));

        Assert.Equal(200, rows.Count);
        Assert.Equal(200, rows.Select(r => r.SerialNo).Distinct().Count());
        Assert.Equal(120, rows.Count(r => r.LotNo == s.Code("L-CAP-A")));
        Assert.Equal(80, rows.Count(r => r.LotNo == s.Code("L-CAP-B")));
        Assert.All(rows, r => Assert.Equal(0, r.Depth));   // L-CAP-B carries SUP-S1 itself: depth 0 beats the path through P
        // Same supplier lot code on another item is another delivery: nothing.
        var resItem = await api.WithDbAsync(db => db.Items.Where(i => i.Code == "RES-100K").Select(i => i.Id).SingleAsync());
        Assert.Empty(await TraceAsync(q => q.ForwardBySupplierLotAsync(s.Code("SUP-S1"), resItem, default)));
    }

    [Fact]
    public async Task Forward_rows_carry_station_time_and_operator()
    {
        var s = await ScenarioAsync();

        var row = (await TraceAsync(q => q.ForwardByLotAsync(s.Code("L-CAP-A"), default)))[0];

        Assert.Equal((s.SerialNo(1), s.Code("L-CAP-A"), "L1-SMT-01", TraceabilitySeeder.MadeFrom + TraceabilitySeeder.Cycle, "EMP00123"),
            (row.SerialNo, row.LotNo, row.StationCode, row.ConsumedAt, row.OperatorCode));
    }

    [Fact]
    public async Task Backward_from_board_150_finds_the_child_reel_its_parent_and_the_resistor()
    {
        var s = await ScenarioAsync();

        var rows = await TraceAsync(q => q.BackwardAsync(s.SerialNo(150), default));

        Assert.Equal(
            [(0, s.Code("L-CAP-B"), (string?)null, "CAP-100NF", "EMP00123"), (1, s.Code("L-CAP-P"), s.Code("L-CAP-B"), "CAP-100NF", null),
             (0, s.Code("L-RES-A"), null, "RES-100K", "EMP00123")],
            rows.Select(r => (r.Depth, r.LotNo, r.ChildLotNo, r.ItemCode, r.LoadedBy)).ToList());
        Assert.All(rows, r => Assert.Equal(("L1-SMT-01", "EMP00123", TraceabilitySeeder.MadeFrom + TraceabilitySeeder.Cycle * 150),
            (r.StationCode, r.OperatorCode, r.ConsumedAt)));
        Assert.Equal(s.Code("SUP-S1"), rows[1].SupplierLot);
    }

    [Fact]
    public async Task Backward_from_board_50_finds_the_first_reel_and_the_resistor()
    {
        var s = await ScenarioAsync();

        var rows = await TraceAsync(q => q.BackwardAsync(s.SerialNo(50), default));

        Assert.Equal([(0, s.Code("L-CAP-A")), (0, s.Code("L-RES-A"))], rows.Select(r => (r.Depth, r.LotNo)).ToList());
    }

    // A search screen asks for typos all day: empty, not an error.
    [Fact]
    public async Task Unknown_lot_serial_and_supplier_lot_give_empty_lists()
    {
        Assert.Empty(await TraceAsync(q => q.ForwardByLotAsync("L-NO-SUCH-LOT", default)));
        Assert.Empty(await TraceAsync(async q => await q.ForwardBySupplierLotAsync("NO-SUCH-SUP", await CapItemIdAsync(), default)));
        Assert.Empty(await TraceAsync(q => q.BackwardAsync("NO-SUCH-SERIAL", default)));
    }

    // Wrong data on purpose: B -> P added to P -> B (the database accepts it; R12 is a rule of the write path).
    // The depth guard stops every branch, nothing hangs or fails, and each lot appears once.
    [Fact]
    public async Task A_genealogy_cycle_stops_at_the_depth_guard()
    {
        var s = await ScenarioAsync(ApiFixture.Unique("C")[2..]);
        await api.WithDbAsync(async db =>
        {
            db.LotGenealogy.Add(new LotGenealogy(s.LotIds["L-CAP-B"], s.LotIds["L-CAP-P"], 1, DateTime.UtcNow));
            return await db.SaveChangesAsync();
        });

        var forward = await TraceAsync(q => q.ForwardByLotAsync(s.Code("L-CAP-P"), default)).WaitAsync(TimeSpan.FromSeconds(10));
        var backward = await TraceAsync(q => q.BackwardAsync(s.SerialNo(150), default)).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(80, forward.Count);
        Assert.Equal([(0, s.Code("L-CAP-B")), (1, s.Code("L-CAP-P")), (0, s.Code("L-RES-A"))],
            backward.Select(r => (r.Depth, r.LotNo)).ToList());
    }

    // M6 step 9: a .NET string goes out as nvarchar unless told otherwise, and nvarchar against the varchar code
    // columns turned the serial seek into a scan (0.65 -> 83 ms on 625,000 serials). Every key must go out as varchar.
    [Fact]
    public async Task Keys_are_sent_as_varchar_so_the_indexes_can_seek()
    {
        var s = await ScenarioAsync();
        var recorder = new ParameterRecorder();
        var connection = await api.WithDbAsync(db => Task.FromResult(db.Database.GetConnectionString()!));
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(connection).AddInterceptors(recorder).Options);
        var trace = new TraceQueries(db);

        await trace.ForwardByLotAsync(s.Code("L-CAP-A"), default);
        await trace.ForwardBySupplierLotAsync(s.Code("SUP-S1"), await CapItemIdAsync(), default);
        await trace.BackwardAsync(s.SerialNo(150), default);

        var keys = recorder.Parameters.Where(p => p.Value is string).ToList();
        Assert.Equal(["@LotNo", "@SupplierLot", "@SerialNo"], keys.Select(p => p.ParameterName));
        Assert.All(keys, p => Assert.Equal(DbType.AnsiString, p.DbType));
    }

    // A sized varchar parameter would silently cut a longer value: longer than the column = nothing, not a prefix match.
    [Fact]
    public async Task Keys_longer_than_the_column_find_nothing()
    {
        var s = await ScenarioAsync();

        Assert.Empty(await TraceAsync(q => q.ForwardByLotAsync(s.Code("L-CAP-A").PadRight(31, 'X'), default)));
        Assert.Empty(await TraceAsync(q => q.BackwardAsync(s.SerialNo(150).PadRight(31, 'X'), default)));
        Assert.Empty(await TraceAsync(async q => await q.ForwardBySupplierLotAsync(new string('S', 51), await CapItemIdAsync(), default)));
    }

    private sealed class ParameterRecorder : DbCommandInterceptor
    {
        public List<DbParameter> Parameters { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Parameters.AddRange(command.Parameters.Cast<DbParameter>());
            return ValueTask.FromResult(result);
        }
    }
}
