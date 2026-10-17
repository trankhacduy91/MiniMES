using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using MiniMes.Contracts.Planning;
using MiniMes.Domain.Planning;
using MiniMes.IntegrationTests.Infrastructure;

namespace MiniMes.IntegrationTests.Planning;

// Spec M2 step 7: Hold/Resume/Complete/Close/Cancel/Priority over the API with the right roles and rules,
// and the dispatch list. Start() belongs to M3 (first scan), so tests start orders directly in the database,
// and "production" is a counter written in SQL, like M3 will do.
[Collection(ApiCollection.Name)]
public class WorkOrderTransitionTests(ApiFixture api)
{
    private static readonly DateTime Start = new(2026, 11, 2, 1, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Full_life_cycle_release_start_hold_resume_complete_close_is_audited()
    {
        var wo = await InProgressAsync(10);
        var sup = await api.LoginAsync("sup01");

        wo = await OkAsync(sup, wo, "hold", "Suspect IC lot X moisture");
        Assert.Equal(("OnHold", "Suspect IC lot X moisture"), (wo.Status, wo.HoldReason));
        Assert.Equal(["Resume", "ChangePriority"], wo.AllowedActions);
        wo = await OkAsync(sup, wo, "resume");
        await SetLastStepGoodAsync(wo.Id, 10);
        wo = await OkAsync(sup, await GetAsync(sup, wo.Id), "complete");    // planned qty reached: no reason
        Assert.Equal(("Completed", (string?)null), (wo.Status, wo.CompleteReason));
        wo = await OkAsync(sup, wo, "close");

        Assert.Equal("Closed", wo.Status);
        Assert.Empty(wo.AllowedActions);
        var history = await api.WithDbAsync(db => db.AuditLogs
            .Where(a => a.TableName == "WorkOrders" && a.RecordKey == wo.Id.ToString() && a.ColumnName == "Status")
            .OrderBy(a => a.Id).Select(a => a.OldValue + ">" + a.NewValue + " by " + a.ChangedBy).ToListAsync());
        Assert.Equal(["Draft>Released by planner01", "Released>InProgress by system", "InProgress>OnHold by sup01",
            "OnHold>InProgress by sup01", "InProgress>Completed by sup01", "Completed>Closed by sup01"], history);
    }

    [Fact]
    public async Task Short_order_completes_only_with_a_reason()
    {
        var wo = await InProgressAsync(200);
        await SetLastStepGoodAsync(wo.Id, 197);
        wo = await GetAsync(await api.LoginAsync("sup01"), wo.Id);
        var sup = await api.LoginAsync("sup01");

        var noReason = await PostAsync(sup, wo, "complete");
        var withReason = await OkAsync(sup, wo, "complete", "3 pcs scrapped, customer accepts 197");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, noReason.StatusCode);
        Assert.Contains("197 of 200", await noReason.Content.ReadAsStringAsync());
        Assert.Equal(("Completed", "3 pcs scrapped, customer accepts 197"), (withReason.Status, withReason.CompleteReason));
    }

    // Rule R8: cancelling a released WO scraps its 200 unused serials, with the reason, in the same transaction.
    [Fact]
    public async Task Cancel_released_order_scraps_its_serials()
    {
        var planner = await api.LoginAsync("planner01");
        var wo = await ReleasedAsync(200);

        wo = await OkAsync(planner, wo, "cancel", "Customer cancelled the order");

        Assert.Equal(("Cancelled", "Customer cancelled the order"), (wo.Status, wo.CancelReason));
        var serials = await api.WithDbAsync(db => db.Serials.Where(s => s.WorkOrderId == wo.Id).ToListAsync());
        Assert.Equal(200, serials.Count);
        Assert.All(serials, s => Assert.Equal((SerialStatus.Scrapped, "Work order cancelled: Customer cancelled the order"),
            (s.Status, s.ScrapReason)));
        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await planner.PostAsJsonAsync($"/api/work-orders/{wo.Id}/labels/print", new PrintLabelsRequest())).StatusCode);
    }

    [Fact]
    public async Task Cancel_after_output_is_422_and_no_serial_is_scrapped()
    {
        var wo = await InProgressAsync(20);
        await api.WithDbAsync(db => db.WorkOrderOperations.Where(o => o.WorkOrderId == wo.Id && o.Seq == 10)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.QtyGood, 1)));
        wo = await GetAsync(await api.LoginAsync("planner01"), wo.Id);
        Assert.DoesNotContain("Cancel", wo.AllowedActions);

        var response = await PostAsync(await api.LoginAsync("planner01"), wo, "cancel", "Too late");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(0, await api.WithDbAsync(db => db.Serials.CountAsync(s => s.WorkOrderId == wo.Id && s.Status == SerialStatus.Scrapped)));
    }

    [Fact]
    public async Task Cancel_of_a_draft_needs_only_a_reason()
    {
        var planner = await api.LoginAsync("planner01");
        var draft = await WorkOrderApiTests.CreateAsync(planner, await RequestAsync(5));

        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(planner, draft, "cancel")).StatusCode);   // reason missing
        Assert.Equal("Cancelled", (await OkAsync(planner, draft, "cancel", "Created on the wrong line")).Status);
    }

    [Theory]
    [InlineData("hold", "Released")]       // R9: only a running order can be held
    [InlineData("resume", "InProgress")]
    [InlineData("close", "InProgress")]   // R11: close only after complete
    [InlineData("complete", "Released")]
    public async Task Action_in_the_wrong_status_is_422(string action, string status)
    {
        var wo = status == "Released" ? await ReleasedAsync(1) : await InProgressAsync(1);

        var response = await PostAsync(await api.LoginAsync("sup01"), wo, action, "some reason");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Theory]
    [InlineData("planner01", "hold")]       // supervisor's job
    [InlineData("planner01", "complete")]
    [InlineData("sup01", "cancel")]         // planner's job
    [InlineData("sup01", "release")]
    [InlineData("qc01", "close")]
    public async Task Wrong_role_is_403(string user, string action)
    {
        var wo = await InProgressAsync(1);

        var response = await PostAsync(await api.LoginAsync(user), wo, action, "some reason");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Operator_cannot_change_a_work_order()
    {
        var wo = await InProgressAsync(1);

        var response = await PostAsync(await api.LoginOperatorAsync(), wo, "hold", "x");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // The supervisor holds from a screen loaded before the planner changed the priority. The status is still
    // InProgress, so Hold() itself would allow it: only the RowVersion can tell -> 409, nothing changes.
    // (Had the other change been a status change, Hold() would refuse first with 422, as in step 4.)
    [Fact]
    public async Task Hold_with_a_stale_row_version_is_409()
    {
        var wo = await InProgressAsync(1);
        var planner = await api.LoginAsync("planner01");
        (await planner.PutAsJsonAsync($"/api/work-orders/{wo.Id}/priority", new ChangePriorityRequest(2, wo.RowVersion))).EnsureSuccessStatusCode();

        var stale = await PostAsync(await api.LoginAsync("sup01"), wo, "hold", "Old screen");

        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var now = await GetAsync(planner, wo.Id);
        Assert.Equal(("InProgress", 2, (string?)null), (now.Status, now.Priority, now.HoldReason));
    }

    // ---------- [v2] priority ----------

    [Fact]
    public async Task Priority_changes_while_running_and_is_audited()
    {
        var wo = await InProgressAsync(1);
        var sup = await api.LoginAsync("sup01");

        var response = await sup.PutAsJsonAsync($"/api/work-orders/{wo.Id}/priority", new ChangePriorityRequest(1, wo.RowVersion));

        response.EnsureSuccessStatusCode();
        Assert.Equal(1, (await response.Content.ReadFromJsonAsync<WorkOrderDetailDto>())!.Priority);
        var audit = await api.WithDbAsync(db => db.AuditLogs.SingleAsync(a =>
            a.TableName == "WorkOrders" && a.RecordKey == wo.Id.ToString() && a.ColumnName == "Priority"));
        Assert.Equal(("5", "1", "sup01"), (audit.OldValue, audit.NewValue, audit.ChangedBy));
    }

    [Fact]
    public async Task Priority_outside_1_to_9_is_400_and_after_close_is_422()
    {
        var sup = await api.LoginAsync("sup01");
        var running = await InProgressAsync(1);
        var closed = await InProgressAsync(1);
        await SetLastStepGoodAsync(closed.Id, 1);
        closed = await OkAsync(sup, await OkAsync(sup, await GetAsync(sup, closed.Id), "complete"), "close");

        var zero = await sup.PutAsJsonAsync($"/api/work-orders/{running.Id}/priority", new ChangePriorityRequest(0, running.RowVersion));
        var afterClose = await sup.PutAsJsonAsync($"/api/work-orders/{closed.Id}/priority", new ChangePriorityRequest(1, closed.RowVersion));

        Assert.Equal(HttpStatusCode.BadRequest, zero.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, afterClose.StatusCode);
    }

    // ---------- [v2] dispatch list ----------

    // Spec: 3 WOs on a line with priority 5, 5, 1 -> the 1 first, the two 5s by planned start; Draft, Cancelled
    // and Closed orders are not on the list. Line 2 is used only here, so the list holds just these orders.
    [Fact]
    public async Task Dispatch_list_orders_by_priority_then_planned_start_and_hides_finished_orders()
    {
        var planner = await api.LoginAsync("planner01");
        var line2 = await api.WithDbAsync(db => db.Lines.Where(l => l.Code == "L2").Select(l => l.Id).SingleAsync());
        var lateFive = await ReleasedAsync(1, line2, Start.AddHours(2), priority: 5);
        var earlyFive = await ReleasedAsync(1, line2, Start.AddHours(1), priority: 5);
        var urgent = await ReleasedAsync(1, line2, Start.AddHours(3), priority: 1);
        await WorkOrderApiTests.CreateAsync(planner, await RequestAsync(1, line2));                       // Draft
        await OkAsync(planner, await ReleasedAsync(1, line2, Start, priority: 1), "cancel", "Not needed");  // Cancelled

        var list = (await (await api.LoginOperatorAsync()).GetFromJsonAsync<List<DispatchListItemDto>>($"/api/lines/{line2}/dispatch-list"))!;

        Assert.Equal([urgent.WoNo, earlyFive.WoNo, lateFive.WoNo], list.Select(w => w.WoNo));
        Assert.All(list, w => Assert.Equal(("PCB-A", "Released", "Pending"), (w.ItemCode, w.Status, w.FaiStatus)));
        Assert.Equal(HttpStatusCode.NotFound, (await planner.GetAsync("/api/lines/999999/dispatch-list")).StatusCode);
    }

    // ---------- helpers ----------

    private async Task<CreateWorkOrderRequest> RequestAsync(int qty, int lineId = 1, DateTime? start = null, int priority = 5)
    {
        var pcbA = await api.WithDbAsync(db => db.Items.Where(i => i.Code == "PCB-A").Select(i => i.Id).SingleAsync());
        var from = start ?? Start;
        return new CreateWorkOrderRequest(pcbA, lineId, qty, from, from.AddHours(4), priority);
    }

    private async Task<WorkOrderDetailDto> ReleasedAsync(int qty, int lineId = 1, DateTime? start = null, int priority = 5)
    {
        var planner = await api.LoginAsync("planner01");
        var draft = await WorkOrderApiTests.CreateAsync(planner, await RequestAsync(qty, lineId, start, priority));
        return await ReleaseWorkOrderTests.ReleaseOkAsync(planner, draft);
    }

    // Start is M3's first scan; here the test plays that part directly in the database.
    private async Task<WorkOrderDetailDto> InProgressAsync(int qty)
    {
        var released = await ReleasedAsync(qty);
        await api.WithDbAsync(async db =>
        {
            var wo = await db.WorkOrders.SingleAsync(w => w.Id == released.Id);
            wo.Start(DateTime.UtcNow);
            return await db.SaveChangesAsync();
        });
        return await GetAsync(await api.LoginAsync("planner01"), released.Id);
    }

    // Production counted the way M3 will: a set-based UPDATE of the last step's counter.
    private Task<int> SetLastStepGoodAsync(int woId, int good) => api.WithDbAsync(async db =>
    {
        var lastSeq = await db.WorkOrderOperations.Where(o => o.WorkOrderId == woId).MaxAsync(o => o.Seq);
        return await db.WorkOrderOperations.Where(o => o.WorkOrderId == woId && o.Seq == lastSeq)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.QtyGood, good));
    });

    private static async Task<WorkOrderDetailDto> GetAsync(HttpClient client, int id)
        => (await client.GetFromJsonAsync<WorkOrderDetailDto>($"/api/work-orders/{id}"))!;

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, WorkOrderDetailDto wo, string action, string? reason = null)
        => client.PostAsJsonAsync($"/api/work-orders/{wo.Id}/{action}", new WorkOrderTransitionRequest(wo.RowVersion, reason));

    private static async Task<WorkOrderDetailDto> OkAsync(HttpClient client, WorkOrderDetailDto wo, string action, string? reason = null)
    {
        var response = await PostAsync(client, wo, action, reason);
        Assert.True(response.IsSuccessStatusCode, $"{action}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<WorkOrderDetailDto>())!;
    }
}
