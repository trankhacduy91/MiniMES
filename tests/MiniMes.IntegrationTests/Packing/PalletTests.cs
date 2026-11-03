using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using MiniMes.Contracts.Packing;
using MiniMes.Domain.Packing;
using MiniMes.IntegrationTests.Execution;
using MiniMes.IntegrationTests.Infrastructure;

namespace MiniMes.IntegrationTests.Packing;

// Spec M3b step 5 on L1-PACK-01 (PCB-A: 10 per carton, 20 cartons per pallet): palletize, partial carton and pallet,
// unpack, remove from pallet, ship. The open pallet is per (station, item), so it is shared by every test packing
// PCB-A there: each test starts by closing whatever pallet another test left open (FreshPalletAsync).
[Collection(ApiCollection.Name)]
public class PalletTests(ApiFixture api)
{
    private readonly PackTests pack = new(api);
    private readonly ScanTests scan = new(api);

    private async Task FreshPalletAsync()
    {
        var stationId = await scan.StationIdAsync(PackTests.Pack);
        await api.WithDbAsync(db => db.Database.ExecuteSqlAsync(
            $"UPDATE dbo.Pallets SET Status = 'Closed', ClosedAt = SYSUTCDATETIME() WHERE StationId = {stationId} AND Status = 'Open'"));
    }

    // A WO of `boards` passed Test, all packed: full cartons closed, the rest in an open carton. Returns carton numbers.
    private async Task<(int WoId, List<string> Serials, List<string> Cartons, HttpClient Op)> PackedAsync(int boards)
    {
        var (woId, serials) = await pack.PassedTestAsync(boards);
        var op = await api.LoginOperatorAsync(stationCode: PackTests.Pack);
        var cartons = new List<string>();
        foreach (var s in serials)
        {
            var answer = await pack.PackAsync(op, s);
            Assert.Equal("Ok", answer.Result);
            if (!cartons.Contains(answer.CartonNo!)) cartons.Add(answer.CartonNo!);
        }
        return (woId, serials, cartons, op);
    }

    private async Task<PalletizeResponse> PalletizeAsync(HttpClient client, string cartonNo)
    {
        var response = await client.PostAsJsonAsync($"/api/stations/{await scan.StationIdAsync(PackTests.Pack)}/palletize", new PalletizeRequest(cartonNo));
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<PalletizeResponse>())!;
    }

    private static async Task<T> OkAsync<T>(Task<HttpResponseMessage> call)
    {
        var response = await call;
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private Task HoldAsync(string serialNo)
        => api.WithDbAsync(db => db.Database.ExecuteSqlAsync($"UPDATE dbo.Serials SET Status = 'Hold' WHERE SerialNo = {serialNo}"));

    private Task ReleaseAsync(string serialNo)
        => api.WithDbAsync(db => db.Database.ExecuteSqlAsync($"UPDATE dbo.Serials SET Status = 'Completed' WHERE SerialNo = {serialNo}"));

    // Module DoD: 200 boards -> 20 cartons -> 1 full pallet, which closes itself and prints its label.
    [Fact]
    public async Task Two_hundred_boards_fill_twenty_cartons_and_one_pallet()
    {
        await FreshPalletAsync();
        var (_, _, cartons, op) = await PackedAsync(200);
        Assert.Equal(20, cartons.Count);

        var answers = new List<PalletizeResponse>();
        foreach (var c in cartons) answers.Add(await PalletizeAsync(op, c));

        Assert.All(answers, a => Assert.Equal(("Ok", false), (a.Result, a.Duplicate)));
        Assert.Equal(Enumerable.Range(1, 20), answers.Select(a => a.CartonCount));
        Assert.Single(answers.Select(a => a.PalletNo).Distinct());
        Assert.Equal([19], answers.Select((a, i) => (a, i)).Where(x => x.a.PalletClosed).Select(x => x.i));
        var last = answers[^1];
        Assert.True(PackingNumberFormat.IsPalletNo(last.PalletNo!), last.PalletNo);
        Assert.Contains($"^FD{last.PalletNo}^FS", last.LabelZpl);

        var pallet = await op.GetFromJsonAsync<PalletDto>($"/api/pallets/{last.PalletNo}");
        Assert.Equal(("Closed", 20, 200, false, false), (pallet!.Status, pallet.CartonCount, pallet.TotalQty, pallet.IsPartial, pallet.OnHold));
        Assert.Equal(cartons.Order(), pallet.Cartons.Select(c => c.CartonNo));
    }

    // Module DoD: WO of 197 -> 19 full cartons + 1 open with 7; the supervisor closes it short with a reason (P6).
    [Fact]
    public async Task Last_carton_of_a_work_order_is_closed_short_by_a_supervisor()
    {
        var (woId, _, cartons, op) = await PackedAsync(197);
        var last = cartons[^1];
        var supervisor = await api.LoginAsync("sup01");
        var url = $"/api/cartons/{last}/close-partial";

        var byOperator = await op.PostAsJsonAsync(url, new ClosePartialRequest("WO end"));   // kiosk without a badge
        var operatorBadge = await op.PostAsJsonAsync(url, new ClosePartialRequest("WO end", "EMP00123"));
        var noReason = await supervisor.PostAsJsonAsync(url, new ClosePartialRequest(""));
        var closed = await OkAsync<CartonDto>(supervisor.PostAsJsonAsync(url, new ClosePartialRequest("WO end, 7 pcs")));
        var again = await supervisor.PostAsJsonAsync(url, new ClosePartialRequest("again"));

        Assert.Equal(HttpStatusCode.BadRequest, byOperator.StatusCode);
        Assert.Contains("\"SupervisorBadge\"", await byOperator.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, operatorBadge.StatusCode);
        Assert.Contains("not an active supervisor", await operatorBadge.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, noReason.StatusCode);
        Assert.Equal(("Closed", 7, true, "sup01", "WO end, 7 pcs"), (closed.Status, closed.Qty, closed.IsPartial, closed.ClosedBy, closed.CloseReason));
        Assert.Contains("PARTIAL", closed.LabelZpl);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, again.StatusCode);
        var rows = await api.WithDbAsync(db => db.Cartons.Where(c => c.WorkOrderId == woId).Select(c => new { c.Qty, c.IsPartial, c.Status }).ToListAsync());
        Assert.Equal(19, rows.Count(r => r is { Qty: 10, IsPartial: false, Status: CartonStatus.Closed }));
        Assert.Single(rows, r => r is { Qty: 7, IsPartial: true, Status: CartonStatus.Closed });
    }

    // P7: an open carton, a carton with a held board, a label that is no carton, an unknown carton: blocked, nothing
    // written. The same carton scanned twice: shown, not counted twice.
    [Fact]
    public async Task Palletize_refuses_what_must_not_go_on_a_pallet()
    {
        await FreshPalletAsync();
        var (_, serials, cartons, op) = await PackedAsync(25);   // two closed cartons, one open with 5
        await HoldAsync(serials[12]);                             // in the second carton

        var open = await PalletizeAsync(op, cartons[2]);
        var held = await PalletizeAsync(op, cartons[1]);
        var serial = await PalletizeAsync(op, serials[0]);
        var unknown = await PalletizeAsync(op, "C990101999999");
        var first = await PalletizeAsync(op, cartons[0]);
        var twice = await PalletizeAsync(op, cartons[0].ToLowerInvariant());

        Assert.Equal(("Blocked", "CartonNotClosed"), (open.Result, open.ReasonCode));
        Assert.Equal(("Blocked", "CartonOnHold"), (held.Result, held.ReasonCode));
        Assert.Equal(("Blocked", "NotACartonLabel"), (serial.Result, serial.ReasonCode));
        Assert.Equal(("Blocked", "CartonNotFound"), (unknown.Result, unknown.ReasonCode));
        Assert.Equal(("Ok", false, 1), (first.Result, first.Duplicate, first.CartonCount));
        Assert.Equal(("Ok", true, 1, first.PalletNo), (twice.Result, twice.Duplicate, twice.CartonCount, twice.PalletNo));
        var heldCarton = await op.GetFromJsonAsync<CartonDto>($"/api/cartons/{cartons[1]}");
        Assert.Equal((true, (string?)null), (heldCarton!.OnHold, heldCarton.PalletNo));
    }

    // Spec step 5 check + P8: a held board on a pallet stops the shipment; released, it ships; shipped, nothing moves.
    [Fact]
    public async Task Held_serial_blocks_the_shipment_and_a_shipped_pallet_is_frozen()
    {
        await FreshPalletAsync();
        var (_, serials, cartons, op) = await PackedAsync(20);
        foreach (var c in cartons) await PalletizeAsync(op, c);
        var supervisor = await api.LoginAsync("sup01");
        var palletNo = (await op.GetFromJsonAsync<CartonDto>($"/api/cartons/{cartons[0]}"))!.PalletNo!;
        var closed = await OkAsync<PalletDto>(supervisor.PostAsJsonAsync($"/api/pallets/{palletNo}/close-partial", new ClosePartialRequest("Last of WO")));
        await HoldAsync(serials[15]);
        var ship = $"/api/pallets/{palletNo}/ship";

        var onHold = await op.GetFromJsonAsync<PalletDto>($"/api/pallets/{palletNo}");
        var blocked = await supervisor.PostAsJsonAsync(ship, new ShipPalletRequest("DO-2611-015"));
        var byOperator = await op.PostAsJsonAsync(ship, new ShipPalletRequest("DO-2611-015"));
        await ReleaseAsync(serials[15]);
        var shipped = await OkAsync<PalletDto>(supervisor.PostAsJsonAsync(ship, new ShipPalletRequest(" DO-2611-015 ")));

        Assert.Equal(("Closed", true, 2), (closed.Status, closed.IsPartial, closed.CartonCount));
        Assert.Equal([false, true], onHold!.Cartons.Select(c => c.OnHold));
        Assert.True(onHold.OnHold);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, blocked.StatusCode);
        Assert.Contains("held serials", await blocked.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Forbidden, byOperator.StatusCode);
        Assert.Equal(("Shipped", "DO-2611-015", "sup01"), (shipped.Status, shipped.ShipmentRef, shipped.ShippedBy));
        Assert.All(await api.WithDbAsync(db => db.Cartons.Where(c => cartons.Contains(c.CartonNo)).Select(c => c.Status).ToListAsync()),
            s => Assert.Equal(CartonStatus.Shipped, s));

        // Frozen (P8): no unpack, no removal, no second shipment.
        var unpack = await supervisor.PostAsJsonAsync($"/api/cartons/{cartons[0]}/unpack", new UnpackRequest(serials[0], "Customer asked"));
        var remove = await supervisor.PostAsync($"/api/cartons/{cartons[0]}/remove-from-pallet", null);
        var shipAgain = await supervisor.PostAsJsonAsync(ship, new ShipPalletRequest("DO-2611-016"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unpack.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, remove.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, shipAgain.StatusCode);
    }

    // Spec 1.3, 16:00: the defective board comes out of a carton on a pallet. Off the pallet (pallet opens again), out
    // of the carton (carton opens again, history kept), a new board fills it: it closes by itself and prints again.
    // The board taken out can go into another carton later (DoD).
    [Fact]
    public async Task Unpack_from_a_palletized_carton_and_refill()
    {
        await FreshPalletAsync();
        var (_, serials, cartons, op) = await PackedAsync(21);   // two full cartons, one open with 1
        var supervisor = await api.LoginAsync("sup01");
        await PalletizeAsync(op, cartons[0]);
        var palletNo = (await PalletizeAsync(op, cartons[1])).PalletNo!;
        await OkAsync<PalletDto>(supervisor.PostAsJsonAsync($"/api/pallets/{palletNo}/close-partial", new ClosePartialRequest("Test")));
        await OkAsync<CartonDto>(supervisor.PostAsJsonAsync($"/api/cartons/{cartons[2]}/close-partial", new ClosePartialRequest("Make room")));

        var onPallet = await supervisor.PostAsJsonAsync($"/api/cartons/{cartons[0]}/unpack", new UnpackRequest(serials[3], "NCR-0007"));
        var removed = await OkAsync<CartonDto>(supervisor.PostAsync($"/api/cartons/{cartons[0]}/remove-from-pallet", null));
        var notInIt = await supervisor.PostAsJsonAsync($"/api/cartons/{cartons[0]}/unpack", new UnpackRequest(serials[15], "NCR-0007"));
        var unpacked = await OkAsync<CartonDto>(supervisor.PostAsJsonAsync($"/api/cartons/{cartons[0]}/unpack",
            new UnpackRequest(serials[3].ToLowerInvariant(), "NCR-0007 confirmed defect")));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, onPallet.StatusCode);
        Assert.Contains("take it off the pallet", await onPallet.Content.ReadAsStringAsync());
        Assert.Null(removed.PalletNo);
        var pallet = await op.GetFromJsonAsync<PalletDto>($"/api/pallets/{palletNo}");
        Assert.Equal(("Open", 1, false), (pallet!.Status, pallet.CartonCount, pallet.IsPartial));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, notInIt.StatusCode);
        Assert.Equal(("Open", 9, false), (unpacked.Status, unpacked.Qty, unpacked.IsPartial));
        Assert.DoesNotContain(unpacked.Serials, s => s.SerialNo == serials[3]);
        var history = await api.WithDbAsync(db => (from cs in db.CartonSerials
                                                    join s in db.Serials on cs.SerialId equals s.Id
                                                    where s.SerialNo == serials[3]
                                                    select new { cs.UnpackedBy, cs.UnpackReason, Unpacked = cs.UnpackedAt != null }).SingleAsync());
        Assert.Equal(("sup01", "NCR-0007 confirmed defect", true), (history.UnpackedBy, history.UnpackReason, history.Unpacked));

        // The reopened carton is the station's open carton of the WO: the unpacked board goes back in and fills it.
        var refill = await pack.PackAsync(op, serials[3]);
        Assert.Equal(("Ok", true, cartons[0], 10, true), (refill.Result, refill.Repacked, refill.CartonNo, refill.CartonQty, refill.CartonClosed));
        Assert.NotNull(refill.LabelZpl);
        Assert.Equal(2, await api.WithDbAsync(db => db.CartonSerials.CountAsync(cs => db.Serials.Any(s => s.Id == cs.SerialId && s.SerialNo == serials[3]))));
    }

    // P4 in words: reopening a carton while the station fills another one of the same WO is refused (close that first).
    [Fact]
    public async Task Unpack_refuses_to_reopen_while_another_carton_of_the_wo_is_open()
    {
        var (_, serials, cartons, _) = await PackedAsync(12);   // carton 1 full, carton 2 open with 2
        var supervisor = await api.LoginAsync("sup01");

        var refused = await supervisor.PostAsJsonAsync($"/api/cartons/{cartons[0]}/unpack", new UnpackRequest(serials[0], "NCR"));
        var fromOpen = await OkAsync<CartonDto>(supervisor.PostAsJsonAsync($"/api/cartons/{cartons[1]}/unpack", new UnpackRequest(serials[11], "Wrong board")));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Contains("another open carton", await refused.Content.ReadAsStringAsync());
        Assert.Equal(("Open", 1), (fromOpen.Status, fromOpen.Qty));
    }

    // 2.3 at pallet level: a pallet at 19/20 and two cartons scanned at the same instant by two operators. One goes on
    // (the pallet closes), the other opens the next pallet: never 21. Like the repack race of step 3, 5 rounds, and the
    // state is made in SQL (19 cartons counted, two closed cartons) so a round costs two requests, not 210 packs.
    [Fact]
    public async Task Two_cartons_at_once_onto_the_last_place_never_overfill_the_pallet()
    {
        var (a, b) = (await api.LoginOperatorAsync(stationCode: PackTests.Pack), await api.LoginOperatorAsync(stationCode: PackTests.Pack));
        var (woId, _) = await pack.PassedTestAsync(1);
        var stationId = await scan.StationIdAsync(PackTests.Pack);
        for (var round = 0; round < 5; round++)
        {
            await FreshPalletAsync();
            var (itemId, cartonNos) = await api.WithDbAsync(async db =>
            {
                var itemId = await db.WorkOrders.Where(w => w.Id == woId).Select(w => w.ItemId).SingleAsync();
                var now = DateTime.UtcNow;
                var pallet = new Pallet(ApiFixture.Unique("PT")[..11], itemId, stationId, now);
                var cartons = Enumerable.Range(0, 2).Select(_ => new Carton($"C{Random.Shared.NextInt64(100_000_000_000, 999_999_999_999)}", woId, itemId, stationId, now)).ToList();
                db.Pallets.Add(pallet);
                foreach (var carton in cartons)
                {
                    db.Cartons.Add(carton);
                    await db.SaveChangesAsync();   // one Open carton per (station, WO) at a time: close each before the next
                    await db.Database.ExecuteSqlAsync($"UPDATE dbo.Cartons SET Status = 'Closed', Qty = 10, ClosedAt = SYSUTCDATETIME() WHERE Id = {carton.Id}");
                }
                await db.Database.ExecuteSqlAsync($"UPDATE dbo.Pallets SET CartonCount = 19 WHERE Id = {pallet.Id}");
                return (itemId, cartons.Select(c => c.CartonNo).ToList());
            });

            var answers = await ScanTests.TogetherAsync(() => PalletizeAsync(a, cartonNos[0]), () => PalletizeAsync(b, cartonNos[1]));

            Assert.All(answers, x => Assert.Equal("Ok", x.Result));
            Assert.Single(answers, x => x.PalletClosed);
            var counts = await api.WithDbAsync(db => db.Pallets.Where(p => answers.Select(x => x.PalletNo).Contains(p.PalletNo))
                .Select(p => p.CartonCount).ToListAsync());
            Assert.Equal([1, 20], counts.Order());
        }
    }

    // Step 6: at the kiosk the operator's token closes short with the supervisor's scanned badge (like the M6 FIFO
    // exception); ClosedBy is the supervisor. The Packing screen reads the open pallet from packing-status.
    [Fact]
    public async Task Kiosk_closes_short_with_a_supervisors_badge_and_sees_the_open_pallet()
    {
        await FreshPalletAsync();
        var (woId, _, cartons, op) = await PackedAsync(23);   // two full cartons, one open with 3
        await PalletizeAsync(op, cartons[0]);

        var carton = await OkAsync<CartonDto>(op.PostAsJsonAsync($"/api/cartons/{cartons[2]}/close-partial", new ClosePartialRequest("Work order end", " sup01 ")));
        var status = await op.GetFromJsonAsync<PackingStatusDto>($"/api/stations/{await scan.StationIdAsync(PackTests.Pack)}/packing-status?woId={woId}");

        Assert.Equal(("Closed", true, "sup01", "Work order end"), (carton.Status, carton.IsPartial, carton.ClosedBy, carton.CloseReason));
        Assert.NotNull(carton.LabelZpl);
        Assert.Null(status!.OpenCarton);
        Assert.Equal((20, 1), (status.CartonsPerPallet!.Value, status.OpenPallet!.CartonCount));
        Assert.Equal([cartons[0]], status.OpenPallet.Cartons.Select(c => c.CartonNo));

        await PalletizeAsync(op, cartons[2]);
        var pallet = await OkAsync<PalletDto>(op.PostAsJsonAsync($"/api/pallets/{status.OpenPallet.PalletNo}/close-partial", new ClosePartialRequest("Work order end", "sup01")));
        Assert.Equal(("Closed", true, 2, "sup01"), (pallet.Status, pallet.IsPartial, pallet.CartonCount, pallet.ClosedBy));
    }

    // Spec 2.5: in Pack mode a carton or pallet label is not a board: said in words, nothing written.
    [Fact]
    public async Task Carton_label_scanned_in_pack_mode_is_not_a_board()
    {
        var (woId, _, cartons, op) = await PackedAsync(10);

        var carton = await pack.PackAsync(op, cartons[0]);
        var pallet = await pack.PackAsync(op, "P261103000004");

        Assert.Equal(("Blocked", "NotASerial"), (carton.Result, carton.ReasonCode));
        Assert.Contains("carton label", carton.Message);
        Assert.Equal(("Blocked", "NotASerial"), (pallet.Result, pallet.ReasonCode));
        Assert.Contains("pallet label", pallet.Message);
        Assert.Equal(1, await api.WithDbAsync(db => db.Cartons.CountAsync(c => c.WorkOrderId == woId)));
    }
}