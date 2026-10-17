using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using MiniMes.Contracts.Common;
using MiniMes.Contracts.MasterData;
using MiniMes.Contracts.Planning;
using MiniMes.Domain.Planning;
using MiniMes.IntegrationTests.Infrastructure;
using MiniMes.IntegrationTests.MasterData;

namespace MiniMes.IntegrationTests.Planning;

// Spec M2 step 4: Release = snapshot + serials + audit in one transaction, and the snapshot never moves.
[Collection(ApiCollection.Name)]
public class ReleaseWorkOrderTests(ApiFixture api)
{
    private static readonly DateTime Start = new(2026, 11, 2, 1, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Release_pcb_a_x_200_freezes_the_recipe_and_creates_200_unique_serials()
    {
        var planner = await api.LoginAsync("planner01");
        var draft = await WorkOrderApiTests.CreateAsync(planner, Request(await IdOfAsync("PCB-A")));

        var wo = await ReleaseOkAsync(planner, draft);

        Assert.Equal(("Released", 1, 1, "Pending"), (wo.Status, wo.BomVersion, wo.RoutingVersion, wo.FaiStatus));
        Assert.NotNull(wo.ReleasedAt);
        Assert.Equal(["Start", "Cancel", "ChangePriority", "PrintLabels"], wo.AllowedActions);
        Assert.Equal(8, wo.Materials.Count);                                          // as many rows as the exploded BOM
        Assert.Equal(2472.48m, wo.Materials.Single(m => m.ComponentCode == "RES-10K-0402").RequiredQty);
        Assert.Equal(["SMT", "ASSY", "TEST", "PACK"], wo.Operations.Select(o => o.OperationCode));
        Assert.Equal("/sop/PCB-A/TEST-v1.pdf", wo.Operations.Single(o => o.Seq == 30).SopUrl);   // [v2] R17
        Assert.Equal(30m, wo.Operations.Single(o => o.Seq == 10).IdealCycleTimeSec);

        var serials = await api.WithDbAsync(db => db.Serials.Where(s => s.WorkOrderId == wo.Id).ToListAsync());
        Assert.Equal(200, serials.Count);
        Assert.Equal(200, serials.Select(s => s.SerialNo).Distinct().Count());
        Assert.Equal(200, wo.SerialCount);
        Assert.All(serials, s =>
        {
            Assert.Equal((SerialStatus.Created, 0, wo.ItemId), (s.Status, s.CurrentSeq, s.ItemId));
            Assert.True(SerialFormat.HasValidCheckDigit(s.SerialNo), s.SerialNo);
            Assert.StartsWith("PCBA", s.SerialNo);
        });
        // Consecutive numbers from one reservation (gaps are allowed between releases, not inside one).
        var numbers = serials.Select(s => long.Parse(s.SerialNo[10..16])).Order().ToList();
        Assert.Equal(199, numbers[^1] - numbers[0]);
    }

    [Fact]
    public async Task Serial_date_is_the_plant_calendar_day()
    {
        var planner = await api.LoginAsync("planner01");
        var wo = await ReleaseOkAsync(planner, await WorkOrderApiTests.CreateAsync(planner, Request(await IdOfAsync("PCB-A")) with { PlannedQty = 1 }));

        var serial = await api.WithDbAsync(db => db.Serials.Where(s => s.WorkOrderId == wo.Id).Select(s => s.SerialNo).SingleAsync());

        var vietnamToday = TimeZoneInfo.ConvertTimeBySystemTimeZoneId(wo.ReleasedAt!.Value, "Asia/Ho_Chi_Minh");
        Assert.Equal($"{vietnamToday:yyMMdd}", serial[4..10]);
    }

    [Fact]
    public async Task Release_writes_the_status_change_to_the_audit_log()
    {
        var planner = await api.LoginAsync("planner01");
        var wo = await ReleaseOkAsync(planner, await WorkOrderApiTests.CreateAsync(planner, Request(await IdOfAsync("PCB-A")) with { PlannedQty = 2 }));

        var audit = await api.WithDbAsync(db => db.AuditLogs
            .Where(a => a.TableName == "WorkOrders" && a.RecordKey == wo.Id.ToString() && a.ColumnName == "Status").ToListAsync());

        var row = Assert.Single(audit);
        Assert.Equal(("Draft", "Released", "planner01"), (row.OldValue, row.NewValue, row.ChangedBy));
    }

    [Fact]
    public async Task Without_fai_the_gate_is_not_required()
    {
        var planner = await api.LoginAsync("planner01");
        var draft = await WorkOrderApiTests.CreateAsync(planner, Request(await IdOfAsync("PCB-A")) with { PlannedQty = 1, RequireFai = false });

        Assert.Equal("NotRequired", (await ReleaseOkAsync(planner, draft)).FaiStatus);
    }

    // DoD: approving a new BOM and routing version after the release changes nothing on the WO.
    [Fact]
    public async Task New_bom_and_routing_versions_after_release_do_not_change_the_work_order()
    {
        var admin = await api.LoginAsync("admin");
        var product = await ProductAsync(admin, "Finished");
        var wo = await ReleaseOkAsync(admin, await WorkOrderApiTests.CreateAsync(admin, Request(product.ItemId) with { PlannedQty = 10 }));

        await NewBomVersionAsync(admin, product, qtyPer: 22m);   // was 20
        await NewRoutingVersionAsync(admin, product, cycleSec: 99m);   // was 30

        var after = (await admin.GetFromJsonAsync<WorkOrderDetailDto>($"/api/work-orders/{wo.Id}"))!;
        Assert.Equal((1, 1), (after.BomVersion, after.RoutingVersion));
        Assert.Equal(wo.Materials, after.Materials);
        Assert.Equal(wo.Operations, after.Operations);
        Assert.Equal(200m, after.Materials.Single(m => m.ComponentItemId == product.CapId).RequiredQty);   // 10 x 20
        // A WO released now takes the new versions.
        var preview = await (await admin.PostAsJsonAsync("/api/work-orders/preview", new PreviewWorkOrderRequest(product.ItemId, 10)))
            .Content.ReadFromJsonAsync<WorkOrderPreviewDto>();
        Assert.Equal((2, 2, 220m), (preview!.BomVersion, preview.RoutingVersion,
            preview.Materials.Single(m => m.ComponentItemId == product.CapId).RequiredQty));
    }

    [Fact]
    public async Task Bom_not_approved_blocks_the_release_and_changes_nothing()
    {
        var admin = await api.LoginAsync("admin");
        var item = await BomVersionApiTests.CreateItemAsync(admin, "FG", "Finished");
        (await admin.PostAsJsonAsync("/api/boms", new CreateDraftRequest(item.Id))).EnsureSuccessStatusCode();   // Draft only
        var draft = await WorkOrderApiTests.CreateAsync(admin, Request(item.Id) with { PlannedQty = 5 });

        var response = await admin.PostAsJsonAsync($"/api/work-orders/{draft.Id}/release", new WorkOrderTransitionRequest(draft.RowVersion));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("no approved BOM", await response.Content.ReadAsStringAsync());
        var after = (await admin.GetFromJsonAsync<WorkOrderDetailDto>($"/api/work-orders/{draft.Id}"))!;
        Assert.Equal(("Draft", 0, 0), (after.Status, after.SerialCount, after.Materials.Count));
    }

    [Fact]
    public async Task Releasing_again_is_refused_and_no_extra_serial_appears()
    {
        var planner = await api.LoginAsync("planner01");
        var draft = await WorkOrderApiTests.CreateAsync(planner, Request(await IdOfAsync("PCB-A")) with { PlannedQty = 3 });
        var released = await ReleaseOkAsync(planner, draft);

        var withOldVersion = await planner.PostAsJsonAsync($"/api/work-orders/{draft.Id}/release", new WorkOrderTransitionRequest(draft.RowVersion));
        var withNewVersion = await planner.PostAsJsonAsync($"/api/work-orders/{draft.Id}/release", new WorkOrderTransitionRequest(released.RowVersion));

        // A request arriving AFTER the first one committed is stopped by the status check in Release() (422),
        // whatever RowVersion it carries. 409 is for two requests that overlap in time (step 5).
        Assert.Equal(HttpStatusCode.UnprocessableEntity, withOldVersion.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, withNewVersion.StatusCode);
        Assert.Equal(3, await api.WithDbAsync(db => db.Serials.CountAsync(s => s.WorkOrderId == draft.Id)));
    }

    // Semi-finished items follow the finished good's serial: released without serials of their own.
    [Fact]
    public async Task Semi_finished_order_gets_no_serials()
    {
        var admin = await api.LoginAsync("admin");
        var semi = await ProductAsync(admin, "Semi");

        var wo = await ReleaseOkAsync(admin, await WorkOrderApiTests.CreateAsync(admin, Request(semi.ItemId) with { PlannedQty = 10 }));

        Assert.Equal(("Released", 0, 2), (wo.Status, wo.SerialCount, wo.Materials.Count));
    }

    [Theory]
    [InlineData("qc01")]
    [InlineData("sup01")]
    public async Task Only_planner_and_admin_release(string user)
    {
        var planner = await api.LoginAsync("planner01");
        var draft = await WorkOrderApiTests.CreateAsync(planner, Request(await IdOfAsync("PCB-A")) with { PlannedQty = 1 });
        var other = await api.LoginAsync(user);

        var response = await other.PostAsJsonAsync($"/api/work-orders/{draft.Id}/release", new WorkOrderTransitionRequest(draft.RowVersion));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Serials_are_listed_page_by_page_in_sequence_order()
    {
        var planner = await api.LoginAsync("planner01");
        var wo = await ReleaseOkAsync(planner, await WorkOrderApiTests.CreateAsync(planner, Request(await IdOfAsync("PCB-A")) with { PlannedQty = 120 }));

        var page2 = (await planner.GetFromJsonAsync<PagedResult<SerialDto>>($"/api/work-orders/{wo.Id}/serials?page=2&pageSize=50"))!;

        Assert.Equal((120, 50), (page2.TotalCount, page2.Items.Count));
        Assert.Equal(page2.Items.Select(s => s.SerialNo).Order(StringComparer.Ordinal), page2.Items.Select(s => s.SerialNo));
        Assert.All(page2.Items, s => Assert.Equal("Created", s.Status));
    }

    // ---------- helpers ----------

    private static CreateWorkOrderRequest Request(int itemId) => new(itemId, LineId: 1, 200, Start, Start.AddHours(4));

    private async Task<int> IdOfAsync(string code) => await api.WithDbAsync(db => db.Items.Where(i => i.Code == code).Select(i => i.Id).SingleAsync());

    internal static async Task<WorkOrderDetailDto> ReleaseOkAsync(HttpClient client, WorkOrderDetailDto draft)
    {
        var response = await client.PostAsJsonAsync($"/api/work-orders/{draft.Id}/release", new WorkOrderTransitionRequest(draft.RowVersion));
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<WorkOrderDetailDto>())!;
    }

    private sealed record Product(int ItemId, int PcbId, int CapId, int WorkCenterId);

    // An item of our own (unique codes) with an approved v1 BOM (1 PCB + 20 caps, no scrap) and a 2-step routing,
    // so new versions can be approved without touching the seeded PCB-A other tests rely on.
    private async Task<Product> ProductAsync(HttpClient admin, string type)
    {
        var item = await BomVersionApiTests.CreateItemAsync(admin, "P", type);
        var pcb = await BomVersionApiTests.CreateItemAsync(admin, "PCB", "Raw");
        var cap = await BomVersionApiTests.CreateItemAsync(admin, "CAP", "Raw");
        var wc = await api.WithDbAsync(db => db.WorkCenters.Where(w => w.Code == "WC-SMT").Select(w => w.Id).SingleAsync());
        var product = new Product(item.Id, pcb.Id, cap.Id, wc);
        await NewBomVersionAsync(admin, product, qtyPer: 20m);
        await NewRoutingVersionAsync(admin, product, cycleSec: 30m);
        return product;
    }

    private static async Task NewBomVersionAsync(HttpClient admin, Product p, decimal qtyPer)
    {
        var draft = await Read<BomDto>(await admin.PostAsJsonAsync("/api/boms", new CreateDraftRequest(p.ItemId, CopyFromLatest: false)));
        (await admin.PutAsJsonAsync($"/api/boms/{draft.Id}/lines", new SaveBomLinesRequest(
            [new BomLineInput(p.PcbId, 1m, 0m, 10), new BomLineInput(p.CapId, qtyPer, 0m, 20)]))).EnsureSuccessStatusCode();
        (await admin.PostAsJsonAsync($"/api/boms/{draft.Id}/approve", new ApproveVersionRequest())).EnsureSuccessStatusCode();
    }

    private static async Task NewRoutingVersionAsync(HttpClient admin, Product p, decimal cycleSec)
    {
        var draft = await Read<RoutingDto>(await admin.PostAsJsonAsync("/api/routings", new CreateDraftRequest(p.ItemId, CopyFromLatest: false)));
        (await admin.PutAsJsonAsync($"/api/routings/{draft.Id}/steps", new SaveRoutingStepsRequest(
            [new RoutingStepInput(10, "SMT", p.WorkCenterId, cycleSec, false, true), new RoutingStepInput(20, "ASSY", p.WorkCenterId, 60m, false, true)])))
            .EnsureSuccessStatusCode();
        (await admin.PostAsJsonAsync($"/api/routings/{draft.Id}/approve", new ApproveVersionRequest())).EnsureSuccessStatusCode();
    }

    private static async Task<T> Read<T>(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }
}
