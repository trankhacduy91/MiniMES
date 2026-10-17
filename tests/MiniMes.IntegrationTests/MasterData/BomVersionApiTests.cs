using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MiniMes.Contracts.MasterData;
using MiniMes.Domain.Common;
using MiniMes.Domain.MasterData;
using MiniMes.Domain.MasterData.Boms;
using MiniMes.IntegrationTests.Infrastructure;

namespace MiniMes.IntegrationTests.MasterData;

// Spec M1 step 5: Draft -> Approved lifecycle, lock after approve, validity ranges, cycle check on save.
[Collection(ApiCollection.Name)]
public class BomVersionApiTests(ApiFixture api)
{
    private static readonly DateTime InTwoDays = DateTime.UtcNow.Date.AddDays(2);

    // (a) A uses B is fine; then B uses A must be refused when saving, with the loop spelled out.
    [Fact]
    public async Task B_uses_A_after_A_uses_B_is_400_with_the_cycle_path()
    {
        var admin = await api.LoginAsync("admin");
        var a = await CreateItemAsync(admin, "A", "Finished");
        var b = await CreateItemAsync(admin, "B", "Semi");
        var bomA = await CreateDraftAsync(admin, a.Id);
        (await SaveLinesAsync(admin, bomA.Id, Line(b.Id))).EnsureSuccessStatusCode();
        var bomB = await CreateDraftAsync(admin, b.Id);

        var response = await SaveLinesAsync(admin, bomB.Id, Line(a.Id));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var message = Assert.Single((await ReadErrorsAsync(response))["Lines"]);
        Assert.Contains($"{a.Code} -> {b.Code} -> {a.Code}", message);
    }

    // (b) Approved = locked.
    [Fact]
    public async Task Saving_lines_after_approve_is_422()
    {
        var admin = await api.LoginAsync("admin");
        var (bom, component) = await ApprovedBomAsync(admin);

        var response = await SaveLinesAsync(admin, bom.Id, Line(component.Id, qtyPer: 99));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(1m, (await admin.GetFromJsonAsync<BomDto>($"/api/boms/{bom.Id}"))!.Lines.Single().QtyPer);
    }

    // (c) Approving v2 closes v1 exactly where v2 starts, checked in the database.
    [Fact]
    public async Task Approving_v2_sets_v1_EffectiveTo_to_v2_EffectiveFrom()
    {
        var admin = await api.LoginAsync("admin");
        var (v1, component) = await ApprovedBomAsync(admin);
        var v2 = await CreateDraftAsync(admin, v1.ItemId);
        Assert.Equal((2, component.Id), (v2.Version, v2.Lines.Single().ComponentItemId)); // copied from v1

        var approved = await ApproveAsync(admin, v2.Id, InTwoDays);

        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        var rows = await api.WithDbAsync(db => db.Boms.AsNoTracking().Where(b => b.ItemId == v1.ItemId)
            .OrderBy(b => b.Version).Select(b => new { b.Status, b.EffectiveFrom, b.EffectiveTo }).ToListAsync());
        Assert.Equal(rows[1].EffectiveFrom, rows[0].EffectiveTo);
        Assert.Equal(InTwoDays, rows[1].EffectiveFrom);
        Assert.Null(rows[1].EffectiveTo);
        Assert.All(rows, r => Assert.Equal(VersionStatus.Approved, r.Status));
    }

    // Spec M1 2.4: only current versions form the graph. A v1 used B; A v2 drops B; then B may use A.
    [Fact]
    public async Task Closed_old_version_does_not_count_as_a_cycle()
    {
        var admin = await api.LoginAsync("admin");
        var a = await CreateItemAsync(admin, "A", "Finished");
        var b = await CreateItemAsync(admin, "B", "Semi");
        var other = await CreateItemAsync(admin, "R", "Raw");
        var v1 = await CreateDraftAsync(admin, a.Id);
        (await SaveLinesAsync(admin, v1.Id, Line(b.Id))).EnsureSuccessStatusCode();
        (await ApproveAsync(admin, v1.Id, null)).EnsureSuccessStatusCode();
        var v2 = await CreateDraftAsync(admin, a.Id, copy: false);
        (await SaveLinesAsync(admin, v2.Id, Line(other.Id))).EnsureSuccessStatusCode();
        (await ApproveAsync(admin, v2.Id, InTwoDays)).EnsureSuccessStatusCode();

        var bomB = await CreateDraftAsync(admin, b.Id);
        var response = await SaveLinesAsync(admin, bomB.Id, Line(a.Id));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task V2_starting_before_v1_is_422_and_nothing_changes()
    {
        var admin = await api.LoginAsync("admin");
        var (v1, _) = await ApprovedBomAsync(admin, effectiveFrom: InTwoDays);
        var v2 = await CreateDraftAsync(admin, v1.ItemId);

        var response = await ApproveAsync(admin, v2.Id, null); // "now" is before v1 starts

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Null((await admin.GetFromJsonAsync<BomDto>($"/api/boms/{v1.Id}"))!.EffectiveTo);
    }

    [Fact]
    public async Task Effective_from_in_the_past_is_400()
    {
        var admin = await api.LoginAsync("admin");
        var parent = await CreateItemAsync(admin, "P", "Finished");
        var c = await CreateItemAsync(admin, "C", "Raw");
        var draft = await CreateDraftAsync(admin, parent.Id);
        (await SaveLinesAsync(admin, draft.Id, Line(c.Id))).EnsureSuccessStatusCode();

        var response = await ApproveAsync(admin, draft.Id, DateTime.UtcNow.AddDays(-1));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Second_draft_while_one_is_open_is_422()
    {
        var admin = await api.LoginAsync("admin");
        var parent = await CreateItemAsync(admin, "P", "Finished");
        await CreateDraftAsync(admin, parent.Id);

        var response = await admin.PostAsJsonAsync("/api/boms", new CreateDraftRequest(parent.Id));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    // The pre-check cannot stop two requests at the same instant; the unique indexes do.
    [Fact]
    public async Task Concurrent_drafts_give_one_201_and_one_refusal()
    {
        var admin = await api.LoginAsync("admin");
        var parent = await CreateItemAsync(admin, "P", "Finished");

        var responses = await Task.WhenAll(
            admin.PostAsJsonAsync("/api/boms", new CreateDraftRequest(parent.Id)),
            admin.PostAsJsonAsync("/api/boms", new CreateDraftRequest(parent.Id)));

        var codes = responses.Select(r => r.StatusCode).Order().ToList();
        Assert.Equal(HttpStatusCode.Created, codes[0]);
        Assert.Contains(codes[1], new[] { HttpStatusCode.Conflict, HttpStatusCode.UnprocessableEntity });
        Assert.Single(await admin.GetFromJsonAsync<List<BomSummaryDto>>($"/api/boms?itemId={parent.Id}") ?? []);
    }

    // DoD: (ItemId, Version) is unique. The use case always takes max + 1, so write past it straight to the DB.
    [Fact]
    public async Task Second_version_1_of_the_same_item_is_refused_by_the_unique_index()
    {
        var admin = await api.LoginAsync("admin");
        var (v1, _) = await ApprovedBomAsync(admin);

        await Assert.ThrowsAsync<ConflictException>(() => api.WithDbAsync(db =>
        {
            db.Boms.Add(Bom.CreateDraft(v1.ItemId, version: 1)); // a Draft, so only the (ItemId, Version) index can stop it
            return db.SaveChangesAsync();
        }));
    }

    [Fact]
    public async Task Raw_item_cannot_get_a_bom_and_inactive_component_cannot_be_used()
    {
        var admin = await api.LoginAsync("admin");
        var raw = await CreateItemAsync(admin, "R", "Raw");
        var parent = await CreateItemAsync(admin, "P", "Finished");
        (await admin.PostAsync($"/api/items/{raw.Id}/deactivate", null)).EnsureSuccessStatusCode();
        var draft = await CreateDraftAsync(admin, parent.Id);

        var rawBom = await admin.PostAsJsonAsync("/api/boms", new CreateDraftRequest(raw.Id));
        var inactiveLine = await SaveLinesAsync(admin, draft.Id, Line(raw.Id));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, rawBom.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, inactiveLine.StatusCode);
    }

    [Fact]
    public async Task Duplicate_component_and_percent_scrap_are_400()
    {
        var admin = await api.LoginAsync("admin");
        var parent = await CreateItemAsync(admin, "P", "Finished");
        var c = await CreateItemAsync(admin, "C", "Raw");
        var draft = await CreateDraftAsync(admin, parent.Id);

        var duplicate = await SaveLinesAsync(admin, draft.Id, Line(c.Id), Line(c.Id));
        var percent = await SaveLinesAsync(admin, draft.Id, Line(c.Id) with { ScrapRate = 2m }); // 2 meaning 2%

        Assert.Equal(HttpStatusCode.BadRequest, duplicate.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, percent.StatusCode);
        Assert.Contains("Lines[0].ScrapRate", (await ReadErrorsAsync(percent)).Keys);
    }

    [Fact]
    public async Task Operator_can_read_but_not_write_boms()
    {
        var op = await api.LoginOperatorAsync();

        Assert.Equal(HttpStatusCode.OK, (await op.GetAsync("/api/boms?itemId=1")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await op.PostAsJsonAsync("/api/boms", new CreateDraftRequest(1))).StatusCode);
    }

    // ---------- helpers ----------

    internal static async Task<ItemDto> CreateItemAsync(HttpClient admin, string prefix, string type)
    {
        var (serialized, lot) = type switch { "Finished" => (true, false), "Raw" => (false, true), _ => (false, false) };
        var response = await admin.PostAsJsonAsync("/api/items",
            new CreateItemRequest(ApiFixture.Unique(prefix), $"{type} {prefix}", type, "pcs", serialized, lot));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ItemDto>())!;
    }

    private static BomLineInput Line(int componentId, decimal qtyPer = 1m) => new(componentId, qtyPer, 0m, 10);

    private static async Task<BomDto> CreateDraftAsync(HttpClient admin, int itemId, bool copy = true)
    {
        var response = await admin.PostAsJsonAsync("/api/boms", new CreateDraftRequest(itemId, copy));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<BomDto>())!;
    }

    private static Task<HttpResponseMessage> SaveLinesAsync(HttpClient admin, int bomId, params BomLineInput[] lines)
        => admin.PutAsJsonAsync($"/api/boms/{bomId}/lines", new SaveBomLinesRequest(lines));

    private static Task<HttpResponseMessage> ApproveAsync(HttpClient admin, int bomId, DateTime? from)
        => admin.PostAsJsonAsync($"/api/boms/{bomId}/approve", new ApproveVersionRequest(from));

    internal static async Task<(BomDto Bom, ItemDto Component)> ApprovedBomAsync(HttpClient admin, DateTime? effectiveFrom = null)
    {
        var parent = await CreateItemAsync(admin, "P", "Finished");
        var component = await CreateItemAsync(admin, "C", "Raw");
        var draft = await CreateDraftAsync(admin, parent.Id);
        (await SaveLinesAsync(admin, draft.Id, Line(component.Id))).EnsureSuccessStatusCode();
        var approved = await ApproveAsync(admin, draft.Id, effectiveFrom);
        approved.EnsureSuccessStatusCode();
        return ((await approved.Content.ReadFromJsonAsync<BomDto>())!, component);
    }

    private static async Task<Dictionary<string, string[]>> ReadErrorsAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("errors").Deserialize<Dictionary<string, string[]>>()!;
    }
}
