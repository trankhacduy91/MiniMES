using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using MiniMes.Contracts.Material;
using MiniMes.Domain.Material;
using MiniMes.IntegrationTests.Infrastructure;

namespace MiniMes.IntegrationTests.Material;

// [v2] Spec M6 step 2b: one IQC result for a whole supplier lot, in one transaction, recorded once.
[Collection(ApiCollection.Name)]
public class IqcTests(ApiFixture api)
{
    private const string Iqc = "/api/material/iqc";

    private Task<int> ItemIdAsync(string code) => api.WithDbAsync(db => db.Items.Where(i => i.Code == code).Select(i => i.Id).SingleAsync());

    // The warehouse receives a delivery; returns its supplier lot.
    private async Task<(int ItemId, string SupplierLot)> ReceiveAsync(int reels, string item = "CAP-100N-0402", string? supplierLot = null)
    {
        var wh = await api.LoginAsync("wh01");
        var itemId = await ItemIdAsync(item);
        supplierLot ??= ApiFixture.Unique("MUR");
        var response = await wh.PostAsJsonAsync("/api/material/lots", new ReceiveLotsRequest(itemId, supplierLot, 10_000, reels, null));
        response.EnsureSuccessStatusCode();
        return (itemId, supplierLot);
    }

    private Task<List<MaterialLotStatus>> StatusesAsync(int itemId, string supplierLot) => api.WithDbAsync(db =>
        db.MaterialLots.Where(l => l.ItemId == itemId && l.SupplierLot == supplierLot).Select(l => l.Status).ToListAsync());

    private Task<int> RecordCountAsync(string supplierLot) => api.WithDbAsync(db => db.IqcRecords.CountAsync(r => r.SupplierLot == supplierLot));

    private static RecordIqcRequest Pass(int itemId, string supplierLot) => new(itemId, supplierLot, 5, 0, "Pass", null, null);

    // Spec check: 3 reels -> Pass -> all 3 Available, one IqcRecord with LotCount = 3.
    [Fact]
    public async Task Pass_releases_every_reel_of_the_supplier_lot_with_one_record()
    {
        var (itemId, supplierLot) = await ReceiveAsync(3);
        var qc = await api.LoginAsync("qc01");
        var pending = await qc.GetFromJsonAsync<List<PendingIqcDto>>($"{Iqc}/pending");
        Assert.Contains(pending!, p => p is { SupplierLot: var s, LotCount: 3, TotalQty: 30_000m, ItemCode: "CAP-100N-0402" } && s == supplierLot);

        var response = await qc.PostAsJsonAsync(Iqc, Pass(itemId, supplierLot));

        response.EnsureSuccessStatusCode();
        var record = (await response.Content.ReadFromJsonAsync<IqcRecordDto>())!;
        Assert.Equal((3, 5, 0, "Pass", "qc01"), (record.LotCount, record.SampleQty, record.DefectQty, record.Result, record.InspectedBy));
        Assert.All(await StatusesAsync(itemId, supplierLot), s => Assert.Equal(MaterialLotStatus.Available, s));
        Assert.Equal(1, await RecordCountAsync(supplierLot));
        Assert.DoesNotContain((await qc.GetFromJsonAsync<List<PendingIqcDto>>($"{Iqc}/pending"))!, p => p.SupplierLot == supplierLot);
        Assert.Contains((await qc.GetFromJsonAsync<List<IqcRecordDto>>($"{Iqc}/records"))!, r => r.Id == record.Id);
    }

    // Spec check: another supplier lot fails -> IqcRejected (never loadable, R17).
    [Fact]
    public async Task Fail_rejects_the_supplier_lot_with_an_incoming_defect_code()
    {
        var (itemId, supplierLot) = await ReceiveAsync(2);
        var qc = await api.LoginAsync("qc01");

        var response = await qc.PostAsJsonAsync(Iqc, new RecordIqcRequest(itemId, supplierLot, 5, 3, "Fail", "INC-WRONG-VALUE", "measured 10 nF"));

        response.EnsureSuccessStatusCode();
        Assert.All(await StatusesAsync(itemId, supplierLot), s => Assert.Equal(MaterialLotStatus.IqcRejected, s));
        var record = (await response.Content.ReadFromJsonAsync<IqcRecordDto>())!;
        Assert.Equal(("Fail", "INC-WRONG-VALUE", 2), (record.Result, record.DefectCode, record.LotCount));
    }

    // Spec check: IQC twice for the same supplier lot -> error, no second record.
    [Fact]
    public async Task Second_result_for_an_inspected_supplier_lot_is_refused()
    {
        var (itemId, supplierLot) = await ReceiveAsync(1);
        var qc = await api.LoginAsync("qc01");
        (await qc.PostAsJsonAsync(Iqc, Pass(itemId, supplierLot))).EnsureSuccessStatusCode();

        var again = await qc.PostAsJsonAsync(Iqc, new RecordIqcRequest(itemId, supplierLot, 5, 3, "Fail", "INC-DAMAGED-REEL", null));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, again.StatusCode);
        Assert.Equal(1, await RecordCountAsync(supplierLot));
        Assert.All(await StatusesAsync(itemId, supplierLot), s => Assert.Equal(MaterialLotStatus.Available, s));   // not flipped
    }

    // Two inspectors press at the same moment: one result, one record; the other gets 422 (the UPDATE found 0 rows).
    [Fact]
    public async Task Two_inspectors_at_once_give_one_result()
    {
        var (itemId, supplierLot) = await ReceiveAsync(3);
        var qc = await api.LoginAsync("qc01");
        var admin = await api.LoginAsync("admin");

        var responses = await Task.WhenAll(qc.PostAsJsonAsync(Iqc, Pass(itemId, supplierLot)), admin.PostAsJsonAsync(Iqc, Pass(itemId, supplierLot)));

        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.UnprocessableEntity], responses.Select(r => r.StatusCode).Order());
        Assert.Equal(1, await RecordCountAsync(supplierLot));
    }

    // A later delivery with the same supplier lot is a new delivery: it waits for its own inspection.
    [Fact]
    public async Task Reels_received_after_the_inspection_wait_for_a_new_one()
    {
        var (itemId, supplierLot) = await ReceiveAsync(2);
        var qc = await api.LoginAsync("qc01");
        (await qc.PostAsJsonAsync(Iqc, Pass(itemId, supplierLot))).EnsureSuccessStatusCode();

        await ReceiveAsync(1, supplierLot: supplierLot);

        Assert.Equal([MaterialLotStatus.Available, MaterialLotStatus.Available, MaterialLotStatus.PendingIqc],
            (await StatusesAsync(itemId, supplierLot)).Order());
        Assert.Contains((await qc.GetFromJsonAsync<List<PendingIqcDto>>($"{Iqc}/pending"))!, p => p.SupplierLot == supplierLot && p.LotCount == 1);
    }

    public static TheoryData<int, string, string?, string?> BadResults() => new()
    {
        { 3, "Fail", null, null },                 // Fail needs a defect code
        { 3, "Fail", "SLD-BRG", null },            // a production defect, not an Incoming one
        { 1, "Pass", null, null },                 // Pass with a defect needs a note (within AQL)
        { 6, "Pass", null, "x" },                  // more defects than samples
        { 0, "Maybe", null, null },                // unknown result
    };

    [Theory]
    [MemberData(nameof(BadResults))]
    public async Task Inconsistent_results_are_refused_and_change_nothing(int defects, string result, string? code, string? note)
    {
        var (itemId, supplierLot) = await ReceiveAsync(1);
        var qc = await api.LoginAsync("qc01");

        var response = await qc.PostAsJsonAsync(Iqc, new RecordIqcRequest(itemId, supplierLot, 5, defects, result, code, note));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.All(await StatusesAsync(itemId, supplierLot), s => Assert.Equal(MaterialLotStatus.PendingIqc, s));
        Assert.Equal(0, await RecordCountAsync(supplierLot));
    }

    [Fact]
    public async Task Pass_with_a_defect_and_a_note_is_accepted()
    {
        var (itemId, supplierLot) = await ReceiveAsync(1);
        var qc = await api.LoginAsync("qc01");

        var response = await qc.PostAsJsonAsync(Iqc, new RecordIqcRequest(itemId, supplierLot, 13, 1, "Pass", null, "AQL 0.65: accept 1"));

        response.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Unknown_supplier_lot_is_not_found()
    {
        var qc = await api.LoginAsync("qc01");

        var response = await qc.PostAsJsonAsync(Iqc, Pass(await ItemIdAsync("CAP-100N-0402"), ApiFixture.Unique("NOPE")));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // Spec check: an operator calling the API gets 403; the warehouse cannot release its own delivery either.
    [Fact]
    public async Task Only_qc_and_admin_record_results()
    {
        var (itemId, supplierLot) = await ReceiveAsync(1);

        var asOperator = await (await api.LoginOperatorAsync()).PostAsJsonAsync(Iqc, Pass(itemId, supplierLot));
        var asWarehouse = await (await api.LoginAsync("wh01")).PostAsJsonAsync(Iqc, Pass(itemId, supplierLot));

        Assert.Equal((HttpStatusCode.Forbidden, HttpStatusCode.Forbidden), (asOperator.StatusCode, asWarehouse.StatusCode));
        Assert.Equal(HttpStatusCode.OK, (await (await api.LoginOperatorAsync()).GetAsync($"{Iqc}/pending")).StatusCode);
    }
}
