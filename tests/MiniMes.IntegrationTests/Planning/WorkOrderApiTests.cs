using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using MiniMes.Contracts.Common;
using MiniMes.Contracts.MasterData;
using MiniMes.Contracts.Planning;
using MiniMes.IntegrationTests.Infrastructure;
using MiniMes.IntegrationTests.MasterData;

namespace MiniMes.IntegrationTests.Planning;

// Spec M2 step 3: preview, create Draft, edit Draft, list. Uses the seeded PCB-A (M1 step 9).
[Collection(ApiCollection.Name)]
public class WorkOrderApiTests(ApiFixture api)
{
    private static readonly DateTime Start = new(2026, 11, 2, 1, 0, 0, DateTimeKind.Utc);   // 08:00 in Vietnam

    // ---------- preview ----------

    [Fact]
    public async Task Preview_pcb_a_x_200_matches_the_hand_calculation()
    {
        var planner = await api.LoginAsync("planner01");

        var preview = await PreviewAsync(planner, await IdOfAsync("PCB-A"), 200);

        Assert.Equal((1, 1), (preview.BomVersion, preview.RoutingVersion));
        var rounded = preview.Materials.ToDictionary(m => m.ComponentCode, m => m.RequiredQtyRounded);
        Assert.Equal(new Dictionary<string, decimal>
        {
            ["PCB-BARE"] = 204, ["RES-10K-0402"] = 2473, ["CAP-100N-0402"] = 4121, ["IC-MCU-QFN"] = 204,
            ["SOLDER-PASTE"] = 0.12726m, ["CONN-USBC"] = 201, ["SHIELD-CAN"] = 201, ["PACK-BAG"] = 200,
        }, rounded);   // 8 bought parts; the Semi PCBA-SMT is made on the line, not issued
        var res = preview.Materials.Single(m => m.ComponentCode == "RES-10K-0402");
        Assert.Equal((10, 2472.48m), (res.OperationSeq, res.RequiredQty));
        Assert.Equal(["SMT", "ASSY", "TEST", "PACK"], preview.Operations.Select(o => o.OperationCode));
        Assert.Equal("/sop/PCB-A/TEST-v1.pdf", preview.Operations.Single(o => o.Seq == 30).SopUrl);
    }

    // Two independent calculations (in-memory tree for the snapshot, SQL CTE for explosion) must agree.
    [Fact]
    public async Task Preview_totals_equal_the_bom_explosion()
    {
        var planner = await api.LoginAsync("planner01");
        var pcbA = await IdOfAsync("PCB-A");

        var preview = await PreviewAsync(planner, pcbA, 200);
        var explosion = await planner.GetFromJsonAsync<BomExplosionDto>($"/api/items/{pcbA}/explosion?qty=200");

        var leaves = explosion!.Lines.Where(l => l.Type == "Raw").ToDictionary(l => l.ItemId, l => l.ExactQty);
        Assert.Equal(leaves.OrderBy(x => x.Key),
            preview.Materials.GroupBy(m => m.ComponentItemId).ToDictionary(g => g.Key, g => g.Sum(m => m.RequiredQty)).OrderBy(x => x.Key));
    }

    [Fact]
    public async Task Preview_of_an_item_without_an_approved_routing_is_422_naming_it()
    {
        var admin = await api.LoginAsync("admin");
        var (bom, _) = await BomVersionApiTests.ApprovedBomAsync(admin);   // approved BOM, no routing

        var response = await admin.PostAsJsonAsync("/api/work-orders/preview", new PreviewWorkOrderRequest(bom.ItemId, 10));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("no approved routing", await response.Content.ReadAsStringAsync());
    }

    // ---------- create ----------

    [Fact]
    public async Task Create_gives_a_draft_with_a_sequence_number_and_nothing_frozen()
    {
        var planner = await api.LoginAsync("planner01");

        var response = await planner.PostAsJsonAsync("/api/work-orders", Request(await IdOfAsync("PCB-A")));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var wo = (await response.Content.ReadFromJsonAsync<WorkOrderDetailDto>())!;
        Assert.Matches(new Regex(@"^WO-\d{4}-\d{5,}$"), wo.WoNo);
        Assert.Equal(("Draft", 200, 5, true, (string?)null), (wo.Status, wo.PlannedQty, wo.Priority, wo.RequireFai, wo.FaiStatus));
        Assert.Equal(("PCB-A", "L1"), (wo.ItemCode, wo.LineCode));
        Assert.Empty(wo.Materials);                       // snapshot happens at Release, not at create
        Assert.Null(wo.BomVersion);
        Assert.Equal(["Edit", "Release", "Cancel", "ChangePriority"], wo.AllowedActions);
        Assert.NotEmpty(wo.RowVersion);
    }

    [Fact]
    public async Task Two_creates_get_increasing_numbers()
    {
        var planner = await api.LoginAsync("planner01");
        var pcbA = await IdOfAsync("PCB-A");

        var a = await CreateAsync(planner, Request(pcbA));
        var b = await CreateAsync(planner, Request(pcbA));

        Assert.True(Sequence(b.WoNo) > Sequence(a.WoNo));
    }

    public static TheoryData<string, Func<CreateWorkOrderRequest, CreateWorkOrderRequest>> BadRequests() => new()
    {
        { "PlannedQty", r => r with { PlannedQty = 0 } },
        { "PlannedQty", r => r with { PlannedQty = 10_001 } },
        { "PlannedEnd", r => r with { PlannedEnd = r.PlannedStart } },
        { "Priority", r => r with { Priority = 10 } },
        { "LineId", r => r with { LineId = 999_999 } },
        { "ItemId", r => r with { ItemId = 999_999 } },
    };

    [Theory]
    [MemberData(nameof(BadRequests))]
    public async Task Invalid_create_is_400_on_the_field(string field, Func<CreateWorkOrderRequest, CreateWorkOrderRequest> change)
    {
        var planner = await api.LoginAsync("planner01");

        var response = await planner.PostAsJsonAsync("/api/work-orders", change(Request(await IdOfAsync("PCB-A"))));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(field, (await ReadErrorsAsync(response)).Keys);
    }

    [Fact]
    public async Task Raw_material_gets_no_work_order()
    {
        var planner = await api.LoginAsync("planner01");

        var response = await planner.PostAsJsonAsync("/api/work-orders", Request(await IdOfAsync("RES-10K-0402")));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Raw material", (await ReadErrorsAsync(response))["ItemId"][0]);
    }

    [Theory]
    [InlineData("qc01")]
    [InlineData("sup01")]
    public async Task Only_planner_and_admin_create(string user)
    {
        var client = await api.LoginAsync(user);

        var response = await client.PostAsJsonAsync("/api/work-orders", Request(await IdOfAsync("PCB-A")));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Operator_can_read_work_orders()
    {
        var op = await api.LoginOperatorAsync();

        Assert.Equal(HttpStatusCode.OK, (await op.GetAsync("/api/work-orders")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await op.PostAsJsonAsync("/api/work-orders/preview", new PreviewWorkOrderRequest(1, 1))).StatusCode);
    }

    // ---------- edit (Draft only) ----------

    [Fact]
    public async Task Draft_can_change_qty_line_and_dates()
    {
        var planner = await api.LoginAsync("planner01");
        var wo = await CreateAsync(planner, Request(await IdOfAsync("PCB-A")) with { PlannedQty = 220 });

        var response = await planner.PutAsJsonAsync($"/api/work-orders/{wo.Id}",
            new UpdateWorkOrderRequest(wo.LineId, 200, Start.AddHours(1), Start.AddHours(5), RequireFai: false, wo.RowVersion));

        response.EnsureSuccessStatusCode();
        var updated = (await response.Content.ReadFromJsonAsync<WorkOrderDetailDto>())!;
        Assert.Equal((200, false, Start.AddHours(1)), (updated.PlannedQty, updated.RequireFai, updated.PlannedStart));
        Assert.NotEqual(wo.RowVersion, updated.RowVersion);
    }

    // The planner edits with what they loaded; someone saved in between -> 409, their change is kept.
    [Fact]
    public async Task Edit_with_a_stale_row_version_is_409()
    {
        var planner = await api.LoginAsync("planner01");
        var wo = await CreateAsync(planner, Request(await IdOfAsync("PCB-A")));
        (await planner.PutAsJsonAsync($"/api/work-orders/{wo.Id}",
            new UpdateWorkOrderRequest(wo.LineId, 150, Start, Start.AddHours(4), true, wo.RowVersion))).EnsureSuccessStatusCode();

        var stale = await planner.PutAsJsonAsync($"/api/work-orders/{wo.Id}",
            new UpdateWorkOrderRequest(wo.LineId, 180, Start, Start.AddHours(4), true, wo.RowVersion));

        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal(150, (await planner.GetFromJsonAsync<WorkOrderDetailDto>($"/api/work-orders/{wo.Id}"))!.PlannedQty);
    }

    [Fact]
    public async Task Edit_without_a_valid_row_version_is_400()
    {
        var planner = await api.LoginAsync("planner01");
        var wo = await CreateAsync(planner, Request(await IdOfAsync("PCB-A")));

        var response = await planner.PutAsJsonAsync($"/api/work-orders/{wo.Id}",
            new UpdateWorkOrderRequest(wo.LineId, 150, Start, Start.AddHours(4), true, "not base64!"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("RowVersion", (await ReadErrorsAsync(response)).Keys);
    }

    // Rule R6 at the API: a non-Draft WO is locked. Release comes in step 4, so cancel stands in for it here.
    [Fact]
    public async Task Edit_after_the_draft_stage_is_422()
    {
        var planner = await api.LoginAsync("planner01");
        var wo = await CreateAsync(planner, Request(await IdOfAsync("PCB-A")));
        await api.WithDbAsync(async db =>
        {
            var entity = await db.WorkOrders.SingleAsync(w => w.Id == wo.Id);
            entity.Cancel("Created on the wrong line");
            return await db.SaveChangesAsync();
        });
        var current = (await planner.GetFromJsonAsync<WorkOrderDetailDto>($"/api/work-orders/{wo.Id}"))!;

        var response = await planner.PutAsJsonAsync($"/api/work-orders/{wo.Id}",
            new UpdateWorkOrderRequest(wo.LineId, 150, Start, Start.AddHours(4), true, current.RowVersion));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    // ---------- list ----------

    [Fact]
    public async Task List_filters_by_status_and_search_and_orders_by_priority()
    {
        var planner = await api.LoginAsync("planner01");
        var pcbA = await IdOfAsync("PCB-A");
        var low = await CreateAsync(planner, Request(pcbA) with { Priority = 9 });
        var urgent = await CreateAsync(planner, Request(pcbA) with { Priority = 1 });

        var page = (await planner.GetFromJsonAsync<PagedResult<WorkOrderSummaryDto>>("/api/work-orders?status=Draft&search=PCB-A&pageSize=200"))!;

        Assert.All(page.Items, w => Assert.Equal("Draft", w.Status));
        var ids = page.Items.Select(w => w.Id).ToList();
        Assert.True(ids.IndexOf(urgent.Id) < ids.IndexOf(low.Id));
        Assert.Equal(0, page.Items.Single(w => w.Id == urgent.Id).QtyGoodLastStep);
        Assert.Equal(HttpStatusCode.BadRequest, (await planner.GetAsync("/api/work-orders?status=Running")).StatusCode);
    }

    // ---------- helpers ----------

    private static CreateWorkOrderRequest Request(int itemId) => new(itemId, LineId: 1, 200, Start, Start.AddHours(4));

    private async Task<int> IdOfAsync(string code) => await api.WithDbAsync(db => db.Items.Where(i => i.Code == code).Select(i => i.Id).SingleAsync());

    private static long Sequence(string woNo) => long.Parse(woNo[(woNo.LastIndexOf('-') + 1)..]);

    internal static async Task<WorkOrderDetailDto> CreateAsync(HttpClient client, CreateWorkOrderRequest request)
    {
        var response = await client.PostAsJsonAsync("/api/work-orders", request);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<WorkOrderDetailDto>())!;
    }

    private static async Task<WorkOrderPreviewDto> PreviewAsync(HttpClient client, int itemId, int qty)
    {
        var response = await client.PostAsJsonAsync("/api/work-orders/preview", new PreviewWorkOrderRequest(itemId, qty));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<WorkOrderPreviewDto>())!;
    }

    private static async Task<Dictionary<string, string[]>> ReadErrorsAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("errors").Deserialize<Dictionary<string, string[]>>()!;
    }
}
