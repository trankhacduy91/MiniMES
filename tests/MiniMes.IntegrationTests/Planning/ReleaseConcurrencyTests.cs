using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using MiniMes.Contracts.Planning;
using MiniMes.IntegrationTests.Infrastructure;
using Xunit.Abstractions;

namespace MiniMes.IntegrationTests.Planning;

// Spec M2 step 5 / DoD: evidence that concurrent releases neither duplicate serials nor release twice.
// Each request is a real HTTP call with its own client, scope and DbContext, released together by a gate,
// like two planners clicking at the same moment. The unique index on SerialNo (PlanningSchemaTests) is the
// guarantee; these tests show the sequence and RowVersion make it never needed in normal operation.
[Collection(ApiCollection.Name)]
public class ReleaseConcurrencyTests(ApiFixture api, ITestOutputHelper output)
{
    private static readonly DateTime Start = new(2026, 11, 2, 1, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Two_concurrent_releases_of_one_work_order_succeed_once_and_create_200_serials()
    {
        // Several rounds; the invariants must hold in every one. Observed: the loser always gets 409, and not from
        // RowVersion: EF sends the child INSERTs before the WorkOrders UPDATE, so the loser waits on the winner's
        // new WorkOrderMaterials rows and then breaks their unique index (2601). RowVersion would stop it next, in
        // the same transaction (docs/incidents.md). 422 = it read the WO only after the winner committed.
        for (var round = 0; round < 5; round++)
        {
            var draft = await DraftAsync(200);
            var (a, b) = (await api.LoginAsync("planner01"), await api.LoginAsync("admin"));

            var responses = await TogetherAsync(() => ReleaseAsync(a, draft), () => ReleaseAsync(b, draft));

            var codes = responses.Select(r => r.StatusCode).ToList();
            output.WriteLine($"round {round}: {string.Join(", ", codes.Select(c => (int)c))}");
            Assert.Single(codes, HttpStatusCode.OK);
            Assert.Contains(codes.Single(c => c != HttpStatusCode.OK), new[] { HttpStatusCode.Conflict, HttpStatusCode.UnprocessableEntity });
            var serials = await SerialNosAsync(draft.Id);
            Assert.Equal(200, serials.Count);
            Assert.Equal(200, serials.Distinct().Count());
            Assert.Equal(8, await api.WithDbAsync(db => db.WorkOrderMaterials.CountAsync(m => m.WorkOrderId == draft.Id)));
        }
    }

    [Fact]
    public async Task Two_work_orders_released_at_the_same_moment_get_disjoint_serials()
    {
        var first = await DraftAsync(200);
        var second = await DraftAsync(200);
        var (a, b) = (await api.LoginAsync("planner01"), await api.LoginAsync("admin"));

        var responses = await TogetherAsync(() => ReleaseAsync(a, first), () => ReleaseAsync(b, second));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        var one = await SerialNosAsync(first.Id);
        var two = await SerialNosAsync(second.Id);
        Assert.Equal((200, 200), (one.Count, two.Count));
        Assert.Empty(one.Intersect(two));
        Assert.Equal(400, one.Concat(two).Distinct().Count());
    }

    // The loser path, made deterministic: the planner loaded the WO, then someone changed it (here: the
    // priority) before they clicked Release. Their RowVersion is stale -> 409, and the whole save rolls back:
    // no snapshot, no serials, still Draft.
    [Fact]
    public async Task Release_with_a_row_version_older_than_the_database_is_409_and_writes_nothing()
    {
        var draft = await DraftAsync(50);
        await api.WithDbAsync(async db =>
        {
            var wo = await db.WorkOrders.SingleAsync(w => w.Id == draft.Id);
            wo.ChangePriority(1);
            return await db.SaveChangesAsync();
        });

        var response = await ReleaseAsync(await api.LoginAsync("planner01"), draft);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var after = await api.WithDbAsync(db => db.WorkOrders.Include(w => w.Materials).Include(w => w.Operations).SingleAsync(w => w.Id == draft.Id));
        Assert.Equal(("Draft", 0, 0), (after.Status.ToString(), after.Materials.Count, after.Operations.Count));
        Assert.Empty(await SerialNosAsync(draft.Id));
    }

    // ---------- helpers ----------

    private async Task<WorkOrderDetailDto> DraftAsync(int qty)
    {
        var pcbA = await api.WithDbAsync(db => db.Items.Where(i => i.Code == "PCB-A").Select(i => i.Id).SingleAsync());
        return await WorkOrderApiTests.CreateAsync(await api.LoginAsync("planner01"),
            new CreateWorkOrderRequest(pcbA, LineId: 1, qty, Start, Start.AddHours(4)));
    }

    private static Task<HttpResponseMessage> ReleaseAsync(HttpClient client, WorkOrderDetailDto draft)
        => client.PostAsJsonAsync($"/api/work-orders/{draft.Id}/release", new WorkOrderTransitionRequest(draft.RowVersion));

    // Both calls wait on one gate and start together; Task.Run so neither runs its synchronous part first.
    private static async Task<HttpResponseMessage[]> TogetherAsync(params Func<Task<HttpResponseMessage>>[] calls)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = calls.Select(call => Task.Run(async () => { await gate.Task; return await call(); })).ToArray();
        gate.SetResult();
        return await Task.WhenAll(tasks);
    }

    private Task<List<string>> SerialNosAsync(int woId)
        => api.WithDbAsync(db => db.Serials.Where(s => s.WorkOrderId == woId).Select(s => s.SerialNo).ToListAsync());
}
