using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using MiniMes.Contracts.Packing;
using MiniMes.Domain.Packing;
using MiniMes.Domain.Planning;
using MiniMes.IntegrationTests.Infrastructure;

namespace MiniMes.IntegrationTests.Packing;

// Spec M3b step 4: a carton that closes prints its label once (logged after the commit); a reprint is PrintNo 2 and
// needs a reason; a carton closed again after an unpack prints again by itself (P11). Labelary is FakeLabelPreview.
[Collection(ApiCollection.Name)]
public class PackingLabelApiTests(ApiFixture api)
{
    private readonly PackTests pack = new(api);

    private sealed record LogRow(int PrintNo, string? Reason, int? WorkOrderId, long? SerialId, string PrintedBy);

    private Task<List<LogRow>> LogsAsync(LabelType type, string key) => api.WithDbAsync(db => db.LabelPrintLogs
        .Where(l => l.LabelType == type && l.TargetKey == key).OrderBy(l => l.PrintNo)
        .Select(l => new LogRow(l.PrintNo, l.Reason, l.WorkOrderId, l.SerialId, l.PrintedBy)).ToListAsync());

    private async Task<(int WoId, List<string> Serials, HttpClient Op, PackResponse Closing)> FullCartonAsync(int boards = 10)
    {
        var (woId, serials) = await pack.PassedTestAsync(boards);
        var op = await api.LoginOperatorAsync(stationCode: PackTests.Pack);
        PackResponse last = null!;
        foreach (var s in serials[..10]) last = await pack.PackAsync(op, s);
        return (woId, serials, op, last);
    }

    [Fact]
    public async Task Closing_a_carton_prints_its_label_once()
    {
        var (woId, serials, op, closing) = await FullCartonAsync();
        var earlier = await pack.PackAsync(op, serials[0]);   // already in: no label again

        Assert.True(closing.CartonClosed);
        Assert.Contains($"^FD{closing.CartonNo}^FS", closing.LabelZpl);
        Assert.Contains("^FDQTY 10 PCS^FS", closing.LabelZpl);
        Assert.Null(earlier.LabelZpl);
        Assert.Equal([new LogRow(1, null, woId, null, "EMP00123")], await LogsAsync(LabelType.Carton, closing.CartonNo!));
    }

    [Fact]
    public async Task Reprint_needs_a_reason_and_gets_print_number_two()
    {
        var (_, _, _, closing) = await FullCartonAsync();
        var supervisor = await api.LoginAsync("sup01");
        var url = $"/api/cartons/{closing.CartonNo}/labels/print";

        var noReason = await supervisor.PostAsJsonAsync(url, new PrintPackingLabelRequest(null));
        var withReason = await supervisor.PostAsJsonAsync(url, new PrintPackingLabelRequest("Label torn"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, noReason.StatusCode);
        var label = (await withReason.Content.ReadFromJsonAsync<PackingLabelDto>())!;
        Assert.Equal((closing.CartonNo, 2), (label.TargetKey, label.PrintNo));
        Assert.Contains($"^FD{closing.CartonNo}^FS", label.Zpl);
        Assert.Equal([(1, (string?)null), (2, "Label torn")], (await LogsAsync(LabelType.Carton, closing.CartonNo!)).Select(l => (l.PrintNo, l.Reason)));
    }

    // An open carton can be previewed (quantity so far) but not printed; a preview writes no log.
    [Fact]
    public async Task Open_carton_is_previewed_not_printed()
    {
        var (_, serials) = await pack.PassedTestAsync(3);
        var op = await api.LoginOperatorAsync(stationCode: PackTests.Pack);
        var packed = await pack.PackAsync(op, serials[0]);

        var preview = await op.PostAsync($"/api/cartons/{packed.CartonNo}/labels/preview", null);
        var print = await op.PostAsJsonAsync($"/api/cartons/{packed.CartonNo}/labels/print", new PrintPackingLabelRequest(null));
        var unknown = await op.PostAsJsonAsync("/api/cartons/C000000999999/labels/print", new PrintPackingLabelRequest(null));

        Assert.Equal("image/png", preview.Content.Headers.ContentType?.MediaType);
        Assert.Equal("4x3", api.Factory.LabelPreview.LastInches);
        Assert.Contains("^FDQTY 1 PCS^FS", api.Factory.LabelPreview.LastZpl);
        Assert.Equal((HttpStatusCode.UnprocessableEntity, HttpStatusCode.NotFound), (print.StatusCode, unknown.StatusCode));
        Assert.Empty(await LogsAsync(LabelType.Carton, packed.CartonNo!));
    }

    // P11: one board unpacked (step 5 API; SQL here), carton open again, one more board fills it: closes by itself and
    // prints again, PrintNo 2 with the reason the system gives.
    [Fact]
    public async Task Carton_closed_again_after_an_unpack_prints_its_label_again()
    {
        var (_, serials, op, closing) = await FullCartonAsync(boards: 11);
        await api.WithDbAsync(db => db.Database.ExecuteSqlAsync($"""
            UPDATE dbo.CartonSerials SET UnpackedAt = SYSUTCDATETIME(), UnpackedBy = 'sup01', UnpackReason = 'NCR-0007'
            WHERE SerialId = (SELECT Id FROM dbo.Serials WHERE SerialNo = {serials[3]});
            UPDATE dbo.Cartons SET Qty = 9, Status = 'Open', ClosedAt = NULL WHERE CartonNo = {closing.CartonNo};
            """));

        var refill = await pack.PackAsync(op, serials[10]);

        Assert.Equal((closing.CartonNo, true), (refill.CartonNo, refill.CartonClosed));
        Assert.NotNull(refill.LabelZpl);
        Assert.Equal([(1, (string?)null), (2, "Carton closed again with 10 pcs.")],
            (await LogsAsync(LabelType.Carton, closing.CartonNo!)).Select(l => (l.PrintNo, l.Reason)));
    }

    // The pallet template (pallets close in step 5: one is built in SQL here).
    [Fact]
    public async Task Closed_pallet_label_counts_its_cartons_and_pieces()
    {
        var (_, serials, op, closing) = await FullCartonAsync(boards: 13);
        foreach (var s in serials[10..]) await pack.PackAsync(op, s);
        var palletNo = ApiFixture.Unique("P")[..10];
        await api.WithDbAsync(async db =>
        {
            var carton = await db.Cartons.SingleAsync(c => c.CartonNo == closing.CartonNo);
            var pallet = new Pallet(palletNo, carton.ItemId, carton.StationId, DateTime.UtcNow);
            db.Pallets.Add(pallet);
            await db.SaveChangesAsync();
            return await db.Database.ExecuteSqlAsync($"""
                UPDATE dbo.Pallets SET Status = 'Closed', ClosedAt = SYSUTCDATETIME(), CartonCount = 1, IsPartial = 1 WHERE Id = {pallet.Id};
                UPDATE dbo.Cartons SET PalletId = {pallet.Id} WHERE Id = {carton.Id};
                """);
        });

        var response = await op.PostAsJsonAsync($"/api/pallets/{palletNo}/labels/print", new PrintPackingLabelRequest(null));

        var label = (await response.Content.ReadFromJsonAsync<PackingLabelDto>())!;
        Assert.Equal(1, label.PrintNo);
        Assert.Contains("^FDCARTONS 1   QTY 10 PCS^FS", label.Zpl);
        Assert.Contains("^FDPARTIAL^FS", label.Zpl);
        Assert.Equal([new LogRow(1, null, null, null, "EMP00123")], await LogsAsync(LabelType.Pallet, palletNo));
    }

    // The CHECK keeps the two kinds apart: a carton label row cannot point at a serial.
    [Fact]
    public async Task Database_refuses_a_carton_label_row_with_a_serial()
    {
        var (_, _, _, closing) = await FullCartonAsync();

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => api.WithDbAsync(db => db.Database.ExecuteSqlAsync($"""
            UPDATE dbo.LabelPrintLogs SET SerialId = (SELECT TOP 1 Id FROM dbo.Serials)
            WHERE LabelType = 'Carton' AND TargetKey = {closing.CartonNo}
            """)));

        Assert.Contains("CK_LabelPrintLogs_SerialTarget", ex.Message);
    }
}
