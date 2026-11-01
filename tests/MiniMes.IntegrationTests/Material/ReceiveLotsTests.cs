using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using MiniMes.Contracts.Common;
using MiniMes.Contracts.Material;
using MiniMes.IntegrationTests.Infrastructure;

namespace MiniMes.IntegrationTests.Material;

// Spec M6 step 2 over HTTP: the warehouse receives reels, gets one lot and one label per reel. The API runs on the
// real clock, so expiry dates are years ahead.
[Collection(ApiCollection.Name)]
public partial class ReceiveLotsTests(ApiFixture api)
{
    private const string Lots = "/api/material/lots";

    [GeneratedRegex(@"^L-\d{6}-(\d{6})$")]
    private static partial Regex LotNoPattern();

    private Task<int> ItemIdAsync(string code) => api.WithDbAsync(db => db.Items.Where(i => i.Code == code).Select(i => i.Id).SingleAsync());

    // Spec check: 3 reels -> 3 lots PendingIqc, Qty = RemainingQty, one label each saying PENDING IQC.
    [Fact]
    public async Task Three_reels_become_three_lots_waiting_for_iqc_with_their_labels()
    {
        var client = await api.LoginAsync("wh01");
        var supplierLot = ApiFixture.Unique("MUR");

        var response = await client.PostAsJsonAsync(Lots,
            new ReceiveLotsRequest(await ItemIdAsync("CAP-100N-0402"), supplierLot, 10_000, 3, new DateOnly(2030, 9, 3)));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var result = (await response.Content.ReadFromJsonAsync<ReceiveLotsResultDto>())!;
        Assert.Equal(3, result.Lots.Count);
        Assert.All(result.Lots, l => Assert.Equal((10_000m, 10_000m, supplierLot, "PendingIqc", "CAP-100N-0402"),
            (l.Qty, l.RemainingQty, l.SupplierLot, l.Status, l.ItemCode)));
        // L-yyMMdd-seq6, consecutive numbers from the sequence, in order.
        var seq = result.Lots.Select(l => long.Parse(LotNoPattern().Match(l.LotNo).Groups[1].Value)).ToList();
        Assert.Equal([seq[0], seq[0] + 1, seq[0] + 2], seq);
        // Expiry 03/09/2030 plant time = blocked from 2030-09-02 17:00 UTC (UTC+7).
        Assert.All(result.Lots, l => Assert.Equal(new DateTime(2030, 9, 2, 17, 0, 0), l.ExpiryAt));
        Assert.Equal(3, Regex.Matches(result.Zpl, @"\^XA").Count);
        Assert.Equal(3, Regex.Matches(result.Zpl, "PENDING IQC").Count);
        Assert.All(result.Lots, l => Assert.Contains($"^FD{l.LotNo}^FS", result.Zpl));
        Assert.Contains("EXP 03 SEP 2030", result.Zpl);

        // Listed, newest first, and audited as created (IAudited: one row per lot).
        var listed = await client.GetFromJsonAsync<PagedResult<MaterialLotDto>>($"{Lots}?search={supplierLot}");
        Assert.Equal(result.Lots.Select(l => l.LotNo).Reverse(), listed!.Items.Select(l => l.LotNo));
        var ids = result.Lots.Select(l => l.Id.ToString()).ToList();
        Assert.Equal(3, await api.WithDbAsync(db => db.AuditLogs.CountAsync(a => a.TableName == "MaterialLots" && ids.Contains(a.RecordKey))));
    }

    // [v2] R15: packing bags are exempt from IQC (SkipIqc in M1): straight to Available, no IQC band on the label.
    [Fact]
    public async Task Item_exempt_from_iqc_is_available_at_once()
    {
        var client = await api.LoginAsync("wh01");

        var result = (await (await client.PostAsJsonAsync(Lots,
            new ReceiveLotsRequest(await ItemIdAsync("PACK-BAG"), ApiFixture.Unique("BAG"), 500, 1, null))).Content
            .ReadFromJsonAsync<ReceiveLotsResultDto>())!;

        Assert.Equal(("Available", (DateTime?)null), (result.Lots[0].Status, result.Lots[0].ExpiryAt));
        Assert.DoesNotContain("PENDING IQC", result.Zpl);
        Assert.Contains("EXP NONE", result.Zpl);
    }

    public static TheoryData<string, string, decimal, int, int> BadRequests() => new()
    {
        { "PCB-A", "ItemId", 10, 1, 365 },          // a finished good is serialized, not lot-controlled
        { "CAP-100N-0402", "QtyPerReel", 1.5m, 1, 365 },   // pieces are whole
        { "CAP-100N-0402", "QtyPerReel", 0, 1, 365 },
        { "CAP-100N-0402", "ReelCount", 10, 0, 365 },
        { "CAP-100N-0402", "ReelCount", 10, 101, 365 },
        { "CAP-100N-0402", "ExpiryDate", 10, 1, -1 },  // expired before it arrives
    };

    [Theory]
    [MemberData(nameof(BadRequests))]
    public async Task Wrong_receipts_are_refused_with_a_message_on_the_field(string item, string field, decimal qty, int reels, int expiryInDays)
    {
        var client = await api.LoginAsync("wh01");
        var expiry = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(expiryInDays);
        var supplierLot = ApiFixture.Unique("BAD");

        var response = await client.PostAsJsonAsync(Lots, new ReceiveLotsRequest(await ItemIdAsync(item), supplierLot, qty, reels, expiry));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains($"\"{field}\"", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, await api.WithDbAsync(db => db.MaterialLots.CountAsync(l => l.SupplierLot == supplierLot)));
    }

    // Receiving is the warehouse's job; everybody reads.
    [Theory]
    [InlineData("qc01")]
    [InlineData("planner01")]
    public async Task Other_roles_cannot_receive_but_can_read(string user)
    {
        var client = await api.LoginAsync(user);

        var post = await client.PostAsJsonAsync(Lots, new ReceiveLotsRequest(await ItemIdAsync("CAP-100N-0402"), "S", 1, 1, null));

        Assert.Equal(HttpStatusCode.Forbidden, post.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(Lots)).StatusCode);
    }

    [Fact]
    public async Task Operator_cannot_receive()
    {
        var client = await api.LoginOperatorAsync();

        var post = await client.PostAsJsonAsync(Lots, new ReceiveLotsRequest(await ItemIdAsync("CAP-100N-0402"), "S", 1, 1, null));

        Assert.Equal(HttpStatusCode.Forbidden, post.StatusCode);
    }

    // A torn label: the same ZPL again, and a preview through Labelary (faked in tests).
    [Fact]
    public async Task Label_can_be_printed_again_and_previewed()
    {
        var client = await api.LoginAsync("wh01");
        var lot = (await (await client.PostAsJsonAsync(Lots,
            new ReceiveLotsRequest(await ItemIdAsync("RES-10K-0402"), ApiFixture.Unique("YAG"), 5000, 1, null))).Content
            .ReadFromJsonAsync<ReceiveLotsResultDto>())!.Lots[0];

        var zpl = await client.GetStringAsync($"{Lots}/{lot.Id}/label");
        var png = await client.GetByteArrayAsync($"{Lots}/{lot.Id}/label/preview");

        Assert.Contains($"^FD{lot.LotNo}^FS", zpl);
        Assert.Equal(FakeLabelPreview.Png, png);
        Assert.Equal(zpl, api.Factory.LabelPreview.LastZpl);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{Lots}/999999999/label")).StatusCode);
    }
}
