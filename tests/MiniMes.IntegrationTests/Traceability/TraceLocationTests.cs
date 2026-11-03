using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MiniMes.Contracts.Traceability;
using MiniMes.Domain.Packing;
using MiniMes.Infrastructure.Persistence.Seed;
using MiniMes.IntegrationTests.Infrastructure;

namespace MiniMes.IntegrationTests.Traceability;

// [v2] Spec M6 2.2b / step 7 check, done with M3b: the forward trace says where each board is now. A seeded scenario
// (L-CAP-B -> boards 121-200) packed by hand on L1-PACK-01:
//   carton A  121-130  on pallet P1, shipped with DO-T1      -> Shipped
//   carton B  131-140  on pallet P2, closed (in the store)    -> OnPallet
//   carton C  141-146  open                                   -> InCarton
//   carton D  had 146, unpacked (then packed into C)          -> 146 shows carton C only
//   147-200   completed, not packed                           -> CompletedUnpacked
[Collection(ApiCollection.Name)]
public class TraceLocationTests(ApiFixture api)
{
    private static readonly DateTime T0 = new(2026, 11, 3, 6, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Forward_trace_tells_shipped_on_pallet_in_carton_and_unpacked_apart()
    {
        TraceabilitySeeder.Scenario s;
        using (var scope = api.Factory.Services.CreateScope())
            s = await scope.ServiceProvider.GetRequiredService<TraceabilitySeeder>().SeedAsync(ApiFixture.Unique("L")[2..]);
        var no = new Dictionary<string, string>();
        await api.WithDbAsync(async db =>
        {
            var station = await db.Stations.Where(st => st.Code == "L1-PACK-01").Select(st => st.Id).SingleAsync();
            var itemId = await db.WorkOrders.Where(w => w.Id == s.WorkOrderId).Select(w => w.ItemId).SingleAsync();
            var serialIds = await db.Serials.Where(x => x.WorkOrderId == s.WorkOrderId).ToDictionaryAsync(x => x.SerialNo, x => x.Id);
            long Board(int n) => serialIds[s.SerialNo(n)];
            string Code(string prefix) => prefix + Random.Shared.NextInt64(100_000_000_000, 999_999_999_999);

            // One Open carton/pallet per station at a time (P4): each is closed (or shipped) before the next is opened.
            async Task<int> CartonAsync(string name, IEnumerable<int> boards, string status, int? palletId = null)
            {
                var carton = new Carton(Code("C"), s.WorkOrderId, itemId, station, T0);
                db.Cartons.Add(carton);
                await db.SaveChangesAsync();
                foreach (var b in boards) db.CartonSerials.Add(new CartonSerial(carton.Id, Board(b), "EMP00123", T0));
                await db.SaveChangesAsync();
                await db.Database.ExecuteSqlAsync($"UPDATE dbo.Cartons SET Status = {status}, Qty = {boards.Count()}, PalletId = {palletId} WHERE Id = {carton.Id}");
                no[name] = carton.CartonNo;
                return carton.Id;
            }
            async Task<int> PalletAsync(string name, string status, string? shipmentRef)
            {
                await db.Database.ExecuteSqlAsync($"UPDATE dbo.Pallets SET Status = 'Closed' WHERE StationId = {station} AND Status = 'Open'");
                var pallet = new Pallet(Code("P"), itemId, station, T0);
                db.Pallets.Add(pallet);
                await db.SaveChangesAsync();
                await db.Database.ExecuteSqlAsync($"UPDATE dbo.Pallets SET Status = {status}, ShipmentRef = {shipmentRef} WHERE Id = {pallet.Id}");
                no[name] = pallet.PalletNo;
                return pallet.Id;
            }

            await CartonAsync("A", Enumerable.Range(121, 10), "Shipped", await PalletAsync("P1", "Shipped", "DO-T1"));
            await CartonAsync("B", Enumerable.Range(131, 10), "Closed", await PalletAsync("P2", "Closed", null));
            var d = await CartonAsync("D", [146], "Closed");
            await db.Database.ExecuteSqlAsync($"UPDATE dbo.CartonSerials SET UnpackedAt = {T0.AddMinutes(5)}, UnpackedBy = 'sup01', UnpackReason = 'Test' WHERE CartonId = {d}");
            await CartonAsync("C", Enumerable.Range(141, 6), "Open");
            return 0;
        });

        var qc = await api.LoginAsync("qc01");
        var result = (await qc.GetFromJsonAsync<ForwardTraceResultDto>($"/api/trace/forward?lotNo={s.Code("L-CAP-B")}"))!;

        Assert.Equal([("Shipped", 10), ("OnPallet", 10), ("InCarton", 6), ("CompletedUnpacked", 54)],
            result.ByLocation.Select(l => (l.Location, l.Count)));
        Assert.Equal(80, result.Serials.Count);   // LEFT JOIN: unpacked boards are still there
        ForwardTraceDto Row(int board) => result.Serials.Single(r => r.SerialNo == s.SerialNo(board));
        Assert.Equal(("Shipped", no["A"], no["P1"], "DO-T1"), (Row(125).Location, Row(125).CartonNo, Row(125).PalletNo, Row(125).ShipmentRef));
        Assert.Equal(("OnPallet", no["B"], no["P2"], (string?)null), (Row(131).Location, Row(131).CartonNo, Row(131).PalletNo, Row(131).ShipmentRef));
        Assert.Equal(("InCarton", no["C"], (string?)null), (Row(146).Location, Row(146).CartonNo, Row(146).PalletNo));   // not carton D
        Assert.Equal(("CompletedUnpacked", (string?)null), (Row(200).Location, Row(200).CartonNo));

        // The Excel sheet has the same carton / pallet / delivery order columns (filled now).
        var xlsx = await qc.GetByteArrayAsync($"/api/trace/forward.xlsx?lotNo={s.Code("L-CAP-B")}");
        using var book = new ClosedXML.Excel.XLWorkbook(new MemoryStream(xlsx));
        var sheet = book.Worksheet("Serials");
        var shipped = sheet.RowsUsed().Single(r => r.Cell(1).GetString() == s.SerialNo(125));
        Assert.Equal((no["A"], no["P1"], "DO-T1"), (shipped.Cell(10).GetString(), shipped.Cell(11).GetString(), shipped.Cell(12).GetString()));
    }
}
