using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using MiniMes.Contracts.MasterData;
using MiniMes.Domain.MasterData.Equipment;
using MiniMes.IntegrationTests.Infrastructure;

namespace MiniMes.IntegrationTests.MasterData;

// Routing follows the BOM lifecycle; these cover what differs: Seq rules, work centers, cycle-time range.
[Collection(ApiCollection.Name)]
public class RoutingVersionApiTests(ApiFixture api)
{
    [Fact]
    public async Task Draft_save_approve_then_v2_closes_v1()
    {
        var admin = await api.LoginAsync("admin");
        var (item, wc) = await SetupAsync(admin);
        var v1 = await CreateDraftAsync(admin, item.Id);
        (await SaveStepsAsync(admin, v1.Id, Step(20, "TEST", wc, inspection: true), Step(10, "SMT", wc))).EnsureSuccessStatusCode();
        (await ApproveAsync(admin, v1.Id, null)).EnsureSuccessStatusCode();
        var v2 = await CreateDraftAsync(admin, item.Id);
        var from = DateTime.UtcNow.Date.AddDays(3);

        var approved = await (await ApproveAsync(admin, v2.Id, from)).Content.ReadFromJsonAsync<RoutingDto>();

        Assert.Equal([10, 20], approved!.Steps.Select(s => s.Seq));           // copied and ordered by Seq
        Assert.Equal(from, (await admin.GetFromJsonAsync<RoutingDto>($"/api/routings/{v1.Id}"))!.EffectiveTo);
        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await SaveStepsAsync(admin, v1.Id, Step(10, "SMT", wc))).StatusCode); // approved = locked
    }

    [Fact]
    public async Task Duplicate_seq_and_cycle_time_in_minutes_are_400()
    {
        var admin = await api.LoginAsync("admin");
        var (item, wc) = await SetupAsync(admin);
        var draft = await CreateDraftAsync(admin, item.Id);

        var duplicate = await SaveStepsAsync(admin, draft.Id, Step(10, "SMT", wc), Step(10, "ASSY", wc));
        var tooSlow = await SaveStepsAsync(admin, draft.Id, Step(10, "SMT", wc) with { IdealCycleTimeSec = 7200m });

        Assert.Equal(HttpStatusCode.BadRequest, duplicate.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, tooSlow.StatusCode);
    }

    [Fact]
    public async Task Empty_routing_cannot_be_approved()
    {
        var admin = await api.LoginAsync("admin");
        var (item, _) = await SetupAsync(admin);
        var draft = await CreateDraftAsync(admin, item.Id);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await ApproveAsync(admin, draft.Id, null)).StatusCode);
    }

    [Fact]
    public async Task Unknown_work_center_is_400()
    {
        var admin = await api.LoginAsync("admin");
        var (item, _) = await SetupAsync(admin);
        var draft = await CreateDraftAsync(admin, item.Id);

        Assert.Equal(HttpStatusCode.BadRequest, (await SaveStepsAsync(admin, draft.Id, Step(10, "SMT", 999999))).StatusCode);
    }

    // ---------- helpers ----------

    // Work centers get their API in step 7; until then the test inserts one directly.
    private async Task<(ItemDto Item, int WorkCenterId)> SetupAsync(HttpClient admin)
    {
        var item = await BomVersionApiTests.CreateItemAsync(admin, "FG", "Finished");
        var wcId = await api.WithDbAsync(async db =>
        {
            var lineId = await db.Lines.Select(l => l.Id).FirstAsync();
            var wc = new WorkCenter(lineId, ApiFixture.Unique("WC"), "Test work center", "SMT");
            db.WorkCenters.Add(wc);
            await db.SaveChangesAsync();
            return wc.Id;
        });
        return (item, wcId);
    }

    private static RoutingStepInput Step(int seq, string op, int wc, bool inspection = false)
        => new(seq, op, wc, 30m, inspection, IsMandatory: true);

    private static async Task<RoutingDto> CreateDraftAsync(HttpClient admin, int itemId)
    {
        var response = await admin.PostAsJsonAsync("/api/routings", new CreateDraftRequest(itemId));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<RoutingDto>())!;
    }

    private static Task<HttpResponseMessage> SaveStepsAsync(HttpClient admin, int id, params RoutingStepInput[] steps)
        => admin.PutAsJsonAsync($"/api/routings/{id}/steps", new SaveRoutingStepsRequest(steps));

    private static Task<HttpResponseMessage> ApproveAsync(HttpClient admin, int id, DateTime? from)
        => admin.PostAsJsonAsync($"/api/routings/{id}/approve", new ApproveVersionRequest(from));
}
