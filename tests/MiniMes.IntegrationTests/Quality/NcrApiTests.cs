using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using MiniMes.Contracts.Quality;
using MiniMes.Domain.Quality;
using MiniMes.IntegrationTests.Execution;
using MiniMes.IntegrationTests.Infrastructure;

namespace MiniMes.IntegrationTests.Quality;

// Spec M7 step 8: the NCR workflow through the API, with two different people (qc01 proposes, sup01 approves).
[Collection(ApiCollection.Name)]
public class NcrApiTests(ApiFixture api)
{
    private readonly ScanTests scan = new(api);

    // Step check: Open -> UnderReview -> Dispositioned -> Closed with two users; the audit log keeps who did what.
    [Fact]
    public async Task Two_people_take_an_ncr_from_open_to_closed()
    {
        var (qc, sup) = (await api.LoginAsync("qc01"), await api.LoginAsync("sup01"));
        var board = await BoardAsync();

        var created = await qc.PostAsJsonAsync("/api/ncrs", Request(board));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var ncr = (await created.Content.ReadFromJsonAsync<NcrDto>())!;
        Assert.Matches(@"^NCR-\d{6}$", ncr.NcrNo);
        Assert.Equal(("Open", "qc01", "Serial", board, "Created"), (ncr.Status, ncr.CreatedBy, ncr.Targets[0].Type, ncr.Targets[0].Code, ncr.Targets[0].Status));

        ncr = await OkAsync(qc, $"/api/ncrs/{ncr.Id}/start-review", new NcrTransitionRequest(ncr.RowVersion));
        ncr = await OkAsync(qc, $"/api/ncrs/{ncr.Id}/propose", new ProposeDispositionRequest("Rework", "Reflow U3", null, ncr.RowVersion));
        ncr = await OkAsync(sup, $"/api/ncrs/{ncr.Id}/approve", new NcrTransitionRequest(ncr.RowVersion));
        ncr = await OkAsync(sup, $"/api/ncrs/{ncr.Id}/close", new CloseNcrRequest("CAPA-26-014", ncr.RowVersion));

        Assert.Equal(("Closed", "qc01", "qc01", "sup01", "sup01", "Rework", "CAPA-26-014"),
            (ncr.Status, ncr.ReviewedBy, ncr.ProposedBy, ncr.ApprovedBy, ncr.ClosedBy, ncr.Disposition, ncr.CapaReference));
        var audit = await api.WithDbAsync(db => db.AuditLogs.Where(a => a.TableName == "Ncrs" && a.RecordKey == ncr.Id.ToString() && a.ColumnName == "Status")
            .OrderBy(a => a.Id).Select(a => new { a.NewValue, a.ChangedBy }).ToListAsync());
        Assert.Equal([("UnderReview", "qc01"), ("Dispositioned", "sup01"), ("Closed", "sup01")], audit.Select(a => (a.NewValue!, a.ChangedBy)));
    }

    // Q11 by role and by person: QC cannot approve at all (403); an Admin who proposed cannot approve his own (422).
    [Fact]
    public async Task Nobody_approves_their_own_proposal()
    {
        var (qc, admin) = (await api.LoginAsync("qc01"), await api.LoginAsync("admin"));
        var ncr = await ProposedAsync(admin, "Scrap");

        Assert.Equal(HttpStatusCode.Forbidden, (await qc.PostAsJsonAsync($"/api/ncrs/{ncr.Id}/approve", new NcrTransitionRequest(ncr.RowVersion))).StatusCode);
        var own = await admin.PostAsJsonAsync($"/api/ncrs/{ncr.Id}/approve", new NcrTransitionRequest(ncr.RowVersion));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, own.StatusCode);
        Assert.Contains("another person", await own.Content.ReadAsStringAsync());
    }

    // Someone changed it meanwhile: the old RowVersion gets 409, not a silent overwrite.
    [Fact]
    public async Task A_stale_row_version_is_a_conflict()
    {
        var qc = await api.LoginAsync("qc01");
        var ncr = await CreateAsync(qc, await BoardAsync());
        await OkAsync(qc, $"/api/ncrs/{ncr.Id}/start-review", new NcrTransitionRequest(ncr.RowVersion));

        var stale = await qc.PostAsJsonAsync($"/api/ncrs/{ncr.Id}/cancel", new CancelNcrRequest("Mistake", ncr.RowVersion));

        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
    }

    // Q13 and Q12 through the API.
    [Fact]
    public async Task Return_to_vendor_with_a_serial_and_close_with_an_active_hold_are_refused()
    {
        var (qc, sup) = (await api.LoginAsync("qc01"), await api.LoginAsync("sup01"));
        var board = await BoardAsync();
        var ncr = await CreateAsync(qc, board);
        ncr = await OkAsync(qc, $"/api/ncrs/{ncr.Id}/start-review", new NcrTransitionRequest(ncr.RowVersion));

        var rtv = await qc.PostAsJsonAsync($"/api/ncrs/{ncr.Id}/propose", new ProposeDispositionRequest("ReturnToVendor", "Bad parts", null, ncr.RowVersion));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, rtv.StatusCode);

        ncr = await OkAsync(qc, $"/api/ncrs/{ncr.Id}/propose", new ProposeDispositionRequest("UseAsIs", "Within the deviation", "DEV-2611-01", ncr.RowVersion));
        ncr = await OkAsync(sup, $"/api/ncrs/{ncr.Id}/approve", new NcrTransitionRequest(ncr.RowVersion));
        var serialId = ncr.Targets[0].Id;
        await api.WithDbAsync(db =>
        {
            db.HoldRecords.Add(new HoldRecord(HoldTargetType.Serial, serialId, Guid.NewGuid(), HoldScope.Serials, null, "Containment", ncr.Id, "qc01", DateTime.UtcNow));
            return db.SaveChangesAsync();
        });
        var detail = (await qc.GetFromJsonAsync<NcrDto>($"/api/ncrs/{ncr.Id}"))!;

        var close = await sup.PostAsJsonAsync($"/api/ncrs/{ncr.Id}/close", new CloseNcrRequest(null, detail.RowVersion));

        Assert.Equal(1, detail.ActiveHolds);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, close.StatusCode);
        Assert.Contains("still active", await close.Content.ReadAsStringAsync());
    }

    // "Create NCR" on an SPC alert: linked; the NCR's close closes the alert; a cancel frees it for another NCR.
    [Fact]
    public async Task An_ncr_from_an_spc_alert_carries_it_and_closes_it()
    {
        var (qc, sup) = (await api.LoginAsync("qc01"), await api.LoginAsync("sup01"));
        var board = await BoardAsync();
        var alertId = await AlertAsync();

        var first = await CreateAsync(qc, board, alertId);
        Assert.Equal(("SpcAlert", alertId.ToString(), (long?)alertId), (first.Source, first.SourceRef, first.SpcAlertId));
        await OkAsync(qc, $"/api/ncrs/{first.Id}/cancel", new CancelNcrRequest("Wrong board listed", first.RowVersion));
        Assert.Null(await api.WithDbAsync(db => db.SpcAlerts.Where(a => a.Id == alertId).Select(a => a.NcrId).SingleAsync()));

        var ncr = await CreateAsync(qc, board, alertId);
        ncr = await OkAsync(qc, $"/api/ncrs/{ncr.Id}/start-review", new NcrTransitionRequest(ncr.RowVersion));
        ncr = await OkAsync(qc, $"/api/ncrs/{ncr.Id}/propose", new ProposeDispositionRequest("UseAsIs", "Probe contact, board OK", null, ncr.RowVersion));
        ncr = await OkAsync(sup, $"/api/ncrs/{ncr.Id}/approve", new NcrTransitionRequest(ncr.RowVersion));
        await OkAsync(qc, $"/api/ncrs/{ncr.Id}/close", new CloseNcrRequest(null, ncr.RowVersion));

        var alert = await api.WithDbAsync(db => db.SpcAlerts.SingleAsync(a => a.Id == alertId));
        Assert.Equal((SpcAlertStatus.Closed, (int?)ncr.Id, "qc01"), (alert.Status, alert.NcrId, alert.ClosedBy));
        Assert.StartsWith($"NCR {ncr.NcrNo} closed", alert.CloseNote);
    }

    // Two QC press "Create NCR" on the same alert at once: one NCR, the other gets 409 and keeps nothing.
    [Fact]
    public async Task One_alert_one_ncr_even_when_two_create_at_once()
    {
        var (qc1, qc2) = (await api.LoginAsync("qc01"), await api.LoginAsync("qc01"));
        var board = await BoardAsync();
        var alertId = await AlertAsync();

        var answers = await ScanTests.TogetherAsync(() => qc1.PostAsJsonAsync("/api/ncrs", Request(board, alertId)),
            () => qc2.PostAsJsonAsync("/api/ncrs", Request(board, alertId)));

        Assert.Equal([HttpStatusCode.Created, HttpStatusCode.Conflict], answers.Select(a => a.StatusCode).Order());
        Assert.Equal(1, await api.WithDbAsync(db => db.Ncrs.CountAsync(n => n.SourceRef == alertId.ToString() && n.Source == NcrSource.SpcAlert)));
    }

    [Fact]
    public async Task Unknown_targets_are_listed_and_roles_are_checked()
    {
        var qc = await api.LoginAsync("qc01");
        var bad = await qc.PostAsJsonAsync("/api/ncrs", new CreateNcrRequest("Manual", null, null, "x", 1,
            [new("Serial", "PCBA0000000000000"), new("Lot", "L-NOPE")]));

        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var body = await bad.Content.ReadAsStringAsync();
        Assert.Contains("Serial PCBA0000000000000", body);
        Assert.Contains("Lot L-NOPE", body);
        Assert.Equal(HttpStatusCode.Forbidden, (await (await api.LoginOperatorAsync()).GetAsync("/api/ncrs")).StatusCode);
        Assert.Contains(await qc.GetFromJsonAsync<List<NcrListItemDto>>("/api/ncrs?status=Open") ?? [], n => n.Status == "Open");
    }

    // ---------- helpers ----------

    private static CreateNcrRequest Request(string serialNo, long? alertId = null)
        => new("Inspection", null, "MEAS-OOS", "3V3 rail at the limit on several boards", 1, [new("Serial", serialNo)], alertId);

    private async Task<string> BoardAsync() => (await scan.ReleasedAsync(1)).Serials[0];

    private static async Task<NcrDto> CreateAsync(HttpClient client, string serialNo, long? alertId = null)
    {
        var r = await client.PostAsJsonAsync("/api/ncrs", Request(serialNo, alertId));
        Assert.True(r.StatusCode == HttpStatusCode.Created, $"{(int)r.StatusCode} {await r.Content.ReadAsStringAsync()}");
        return (await r.Content.ReadFromJsonAsync<NcrDto>())!;
    }

    private async Task<NcrDto> ProposedAsync(HttpClient client, string disposition)
    {
        var ncr = await CreateAsync(client, await BoardAsync());
        ncr = await OkAsync(client, $"/api/ncrs/{ncr.Id}/start-review", new NcrTransitionRequest(ncr.RowVersion));
        return await OkAsync(client, $"/api/ncrs/{ncr.Id}/propose", new ProposeDispositionRequest(disposition, "Burnt pads", null, ncr.RowVersion));
    }

    private static async Task<NcrDto> OkAsync<T>(HttpClient client, string url, T body)
    {
        var r = await client.PostAsJsonAsync(url, body);
        Assert.True(r.IsSuccessStatusCode, $"{url}: {(int)r.StatusCode} {await r.Content.ReadAsStringAsync()}");
        return (await r.Content.ReadFromJsonAsync<NcrDto>())!;
    }

    // An open alert on a plan of its own (DataGap: no measurement needed).
    private Task<long> AlertAsync() => api.WithDbAsync(async db =>
    {
        var plan = InspectionPlan.ForMachine(ApiFixture.Unique("NC"), "NCR test", "°C", 235m, 255m, null);
        db.InspectionPlans.Add(plan);
        await db.SaveChangesAsync();
        var alert = new SpcAlert(plan.Id, SpcRuleCode.DataGap, null, DateTime.UtcNow);
        db.SpcAlerts.Add(alert);
        await db.SaveChangesAsync();
        return alert.Id;
    });
}
