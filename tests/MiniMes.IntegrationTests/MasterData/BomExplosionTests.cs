using System.Net;
using System.Net.Http.Json;
using MiniMes.Contracts.MasterData;
using MiniMes.IntegrationTests.Infrastructure;

namespace MiniMes.IntegrationTests.MasterData;

// Spec M1 step 6 / DoD: 200 x PCB-A through the real SQL Server CTE gives the table of spec 1.2.4.
// Codes get a unique suffix because the test database is shared by all tests.
[Collection(ApiCollection.Name)]
public class BomExplosionTests(ApiFixture api)
{
    [Theory]
    [InlineData("PCBA-SMT", "202", 202)]
    [InlineData("PCB-BARE", "203.01", 204)]
    [InlineData("RES-10K-0402", "2472.48", 2473)]
    [InlineData("CAP-100N-0402", "4120.8", 4121)]
    [InlineData("IC-MCU-QFN", "203.01", 204)]
    [InlineData("CONN-USBC", "201", 201)]
    [InlineData("SHIELD-CAN", "201", 201)]
    [InlineData("PACK-BAG", "200", 200)]
    public async Task Pcb_a_times_200_matches_the_spec_table(string code, string exact, int required)
    {
        var (admin, pcb) = await PcbAAsync();

        var line = (await ExplodeAsync(admin, pcb["PCB-A"].Id, 200)).Lines.Single(l => l.ItemId == pcb[code].Id);

        Assert.Equal(decimal.Parse(exact, System.Globalization.CultureInfo.InvariantCulture), line.ExactQty);
        Assert.Equal(required, line.RequiredQty);
    }

    [Fact]
    public async Task Solder_paste_is_0_127_kg_and_nine_lines_come_back()
    {
        var (admin, pcb) = await PcbAAsync();

        var result = await ExplodeAsync(admin, pcb["PCB-A"].Id, 200);

        var solder = result.Lines.Single(l => l.ItemId == pcb["SOLDER-PASTE"].Id);
        Assert.Equal(0.12726m, solder.RequiredQty);      // continuous unit: exact, never issued short
        Assert.Equal(0.127m, Math.Round(solder.ExactQty, 3));
        Assert.Equal(9, result.Lines.Count);             // 8 Raw + the Semi PCBA-SMT
        Assert.Equal("Semi", result.Lines.Single(l => l.ItemId == pcb["PCBA-SMT"].Id).Type);
    }

    // Cross-check: the CTE (SQL) and the in-memory tree (C#) must agree on every total.
    [Fact]
    public async Task Tree_totals_equal_the_cte_result()
    {
        var (admin, pcb) = await PcbAAsync();

        var cte = (await ExplodeAsync(admin, pcb["PCB-A"].Id, 200)).Lines.ToDictionary(l => l.ItemId, l => l.ExactQty);
        var tree = await admin.GetFromJsonAsync<BomTreeNodeDto>($"/api/items/{pcb["PCB-A"].Id}/bom-tree?qty=200");

        var fromTree = new Dictionary<int, decimal>();
        void Add(BomTreeNodeDto n) { foreach (var c in n.Children) { fromTree[c.ItemId] = fromTree.GetValueOrDefault(c.ItemId) + c.RequiredQty; Add(c); } }
        Add(tree!);
        Assert.Equal(cte.OrderBy(x => x.Key), fromTree.OrderBy(x => x.Key));
        Assert.Equal(["PCBA-SMT", "CONN-USBC", "SHIELD-CAN", "PACK-BAG"], tree!.Children.Select(c => c.Code[..^9])); // strip "-XXXXXXXX"
    }

    [Theory]
    [InlineData("explosion", "0")]
    [InlineData("explosion", "-5")]
    [InlineData("bom-tree", "0")]
    public async Task Zero_or_negative_qty_is_400(string endpoint, string qty)
    {
        var admin = await api.LoginAsync("admin");

        var response = await admin.GetAsync($"/api/items/1/{endpoint}?qty={qty}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Semi_without_an_approved_bom_is_422_naming_it()
    {
        var admin = await api.LoginAsync("admin");
        var fg = await BomVersionApiTests.CreateItemAsync(admin, "FG", "Finished");
        var semi = await BomVersionApiTests.CreateItemAsync(admin, "SEMI", "Semi");
        await ApprovedBomAsync(admin, fg.Id, (semi.Id, 1m, 0m));

        var explosion = await admin.GetAsync($"/api/items/{fg.Id}/explosion?qty=10");
        var tree = await admin.GetAsync($"/api/items/{fg.Id}/bom-tree?qty=10");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, explosion.StatusCode);
        Assert.Contains(semi.Code, await explosion.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, tree.StatusCode);
    }

    // A work order for next week must explode the version valid next week.
    [Fact]
    public async Task AsOf_picks_the_version_in_force_at_that_time()
    {
        var admin = await api.LoginAsync("admin");
        var fg = await BomVersionApiTests.CreateItemAsync(admin, "FG", "Finished");
        var raw = await BomVersionApiTests.CreateItemAsync(admin, "RAW", "Raw");
        await ApprovedBomAsync(admin, fg.Id, (raw.Id, 20m, 0m));
        var nextWeek = DateTime.UtcNow.Date.AddDays(7);
        await ApprovedBomAsync(admin, fg.Id, (raw.Id, 22m, 0m), nextWeek);

        var today = await ExplodeAsync(admin, fg.Id, 1);
        var later = await ExplodeAsync(admin, fg.Id, 1, nextWeek);

        Assert.Equal(20m, today.Lines.Single().RequiredQty);
        Assert.Equal(22m, later.Lines.Single().RequiredQty);
    }

    [Fact]
    public async Task Item_without_bom_is_422_and_unknown_item_is_404()
    {
        var admin = await api.LoginAsync("admin");
        var raw = await BomVersionApiTests.CreateItemAsync(admin, "RAW", "Raw");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await admin.GetAsync($"/api/items/{raw.Id}/explosion?qty=1")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/api/items/999999/explosion?qty=1")).StatusCode);
    }

    // ---------- helpers ----------

    private async Task<(HttpClient Admin, Dictionary<string, ItemDto> Items)> PcbAAsync()
    {
        var admin = await api.LoginAsync("admin");
        var items = new Dictionary<string, ItemDto>();
        async Task Add(string code, string type, string uom = "pcs")
        {
            var (serialized, lot) = type switch { "Finished" => (true, false), "Raw" => (false, true), _ => (false, false) };
            var response = await admin.PostAsJsonAsync("/api/items",
                new CreateItemRequest($"{code}-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}", code, type, uom, serialized, lot));
            response.EnsureSuccessStatusCode();
            items[code] = (await response.Content.ReadFromJsonAsync<ItemDto>())!;
        }

        await Add("PCB-A", "Finished");
        await Add("PCBA-SMT", "Semi");
        foreach (var raw in new[] { "PCB-BARE", "RES-10K-0402", "CAP-100N-0402", "IC-MCU-QFN", "CONN-USBC", "SHIELD-CAN", "PACK-BAG" })
            await Add(raw, "Raw");
        await Add("SOLDER-PASTE", "Raw", "kg");

        // Lower level first: approving PCB-A does not need it, but explosion does.
        await ApprovedBomAsync(admin, items["PCBA-SMT"].Id,
            (items["PCB-BARE"].Id, 1m, 0.005m), (items["RES-10K-0402"].Id, 12m, 0.02m), (items["CAP-100N-0402"].Id, 20m, 0.02m),
            (items["IC-MCU-QFN"].Id, 1m, 0.005m), (items["SOLDER-PASTE"].Id, 0.0006m, 0.05m));
        await ApprovedBomAsync(admin, items["PCB-A"].Id,
            (items["PCBA-SMT"].Id, 1m, 0.01m), (items["CONN-USBC"].Id, 1m, 0.005m), (items["SHIELD-CAN"].Id, 1m, 0.005m),
            (items["PACK-BAG"].Id, 1m, 0m));
        return (admin, items);
    }

    // OperationSeq: SMT lines 10, others 20 (PACK-BAG 40 in the spec; not relevant to quantities).
    private static async Task ApprovedBomAsync(HttpClient admin, int itemId, params (int Id, decimal QtyPer, decimal Scrap)[] lines)
        => await ApprovedBomAsync(admin, itemId, lines, effectiveFrom: null);

    private static Task ApprovedBomAsync(HttpClient admin, int itemId, (int Id, decimal QtyPer, decimal Scrap) line, DateTime effectiveFrom)
        => ApprovedBomAsync(admin, itemId, [line], effectiveFrom);

    private static async Task ApprovedBomAsync(HttpClient admin, int itemId, (int Id, decimal QtyPer, decimal Scrap)[] lines, DateTime? effectiveFrom)
    {
        var draft = await admin.PostAsJsonAsync("/api/boms", new CreateDraftRequest(itemId, CopyFromLatest: false));
        draft.EnsureSuccessStatusCode();
        var bom = (await draft.Content.ReadFromJsonAsync<BomDto>())!;
        (await admin.PutAsJsonAsync($"/api/boms/{bom.Id}/lines",
            new SaveBomLinesRequest(lines.Select(l => new BomLineInput(l.Id, l.QtyPer, l.Scrap, 10)).ToList()))).EnsureSuccessStatusCode();
        (await admin.PostAsJsonAsync($"/api/boms/{bom.Id}/approve", new ApproveVersionRequest(effectiveFrom))).EnsureSuccessStatusCode();
    }

    private static async Task<BomExplosionDto> ExplodeAsync(HttpClient admin, int itemId, decimal qty, DateTime? asOf = null)
    {
        var url = $"/api/items/{itemId}/explosion?qty={qty}" + (asOf is { } a ? $"&asOfUtc={a:yyyy-MM-ddTHH:mm:ssZ}" : "");
        var response = await admin.GetAsync(url);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<BomExplosionDto>())!;
    }
}
