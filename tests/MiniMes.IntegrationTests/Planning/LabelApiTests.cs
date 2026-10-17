using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using MiniMes.Contracts.Planning;
using MiniMes.IntegrationTests.Infrastructure;

namespace MiniMes.IntegrationTests.Planning;

// Spec M2 step 6: preview one label, print with a log, reprint = PrintNo + 1 with a reason, only while
// Released/InProgress. The Labelary call is replaced by FakeLabelPreview (no internet in tests).
[Collection(ApiCollection.Name)]
public class LabelApiTests(ApiFixture api)
{
    private static readonly DateTime Start = new(2026, 11, 2, 1, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Preview_returns_a_png_rendered_from_the_first_serial()
    {
        var (planner, wo) = await ReleasedAsync(3);
        var first = (await SerialNosAsync(wo.Id)).Order(StringComparer.Ordinal).First();

        var response = await planner.PostAsJsonAsync($"/api/work-orders/{wo.Id}/labels/preview", new PreviewLabelRequest());

        response.EnsureSuccessStatusCode();
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(FakeLabelPreview.Png, await response.Content.ReadAsByteArrayAsync());
        Assert.Contains($"^FD{first}^FS", api.Factory.LabelPreview.LastZpl);
        Assert.Contains($"^FDPCB-A   {wo.WoNo}^FS", api.Factory.LabelPreview.LastZpl);
        Assert.Equal(0, await LogCountAsync(wo.Id));   // a preview is not a print
    }

    [Fact]
    public async Task Preview_of_a_serial_from_another_order_is_400()
    {
        var (planner, wo) = await ReleasedAsync(1);
        var (_, other) = await ReleasedAsync(1);
        var foreign = (await SerialNosAsync(other.Id)).Single();

        var response = await planner.PostAsJsonAsync($"/api/work-orders/{wo.Id}/labels/preview", new PreviewLabelRequest(foreign));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Print_all_logs_one_row_per_label_and_returns_their_zpl()
    {
        var (planner, wo) = await ReleasedAsync(5);

        var result = await PrintOkAsync(planner, wo, new PrintLabelsRequest());

        Assert.Equal((5, 0), (result.Printed, result.Reprinted));
        Assert.Equal(5, result.Zpl.Split("^XA").Length - 1);
        var logs = await LogsAsync(wo.Id);
        Assert.Equal(5, logs.Count);
        Assert.All(logs, l => Assert.Equal((1, "planner01", (string?)null), (l.PrintNo, l.PrintedBy, l.Reason)));
        Assert.All(await SerialNosAsync(wo.Id), s => Assert.Contains($"^FD{s}^FS", result.Zpl));
    }

    // DoD: printing the same serial twice gives PrintNo 1, 2; the reprint needs (and keeps) a reason.
    [Fact]
    public async Task Reprint_needs_a_reason_and_gets_the_next_print_number()
    {
        var (planner, wo) = await ReleasedAsync(2);
        await PrintOkAsync(planner, wo, new PrintLabelsRequest());
        var serial = (await SerialNosAsync(wo.Id)).Order(StringComparer.Ordinal).First();
        var supervisor = await api.LoginAsync("sup01");

        var noReason = await supervisor.PostAsJsonAsync($"/api/work-orders/{wo.Id}/labels/print", new PrintLabelsRequest([serial]));
        var withReason = await PrintOkAsync(supervisor, wo, new PrintLabelsRequest([serial], "Label burnt in reflow"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, noReason.StatusCode);
        Assert.Equal((1, 1), (withReason.Printed, withReason.Reprinted));
        var history = (await LogsAsync(wo.Id)).Where(l => l.SerialNo == serial).OrderBy(l => l.PrintNo).ToList();
        Assert.Equal([1, 2], history.Select(l => l.PrintNo));
        Assert.Equal(("sup01", "Label burnt in reflow"), (history[1].PrintedBy, history[1].Reason));
        Assert.Equal(3, await LogCountAsync(wo.Id));   // the refused request wrote nothing
    }

    [Fact]
    public async Task Draft_order_has_no_labels()
    {
        var planner = await api.LoginAsync("planner01");
        var draft = await WorkOrderApiTests.CreateAsync(planner, await RequestAsync(1));

        var preview = await planner.PostAsJsonAsync($"/api/work-orders/{draft.Id}/labels/preview", new PreviewLabelRequest());
        var print = await planner.PostAsJsonAsync($"/api/work-orders/{draft.Id}/labels/print", new PrintLabelsRequest());

        Assert.Equal(HttpStatusCode.UnprocessableEntity, preview.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, print.StatusCode);
    }

    [Fact]
    public async Task Unknown_serial_in_the_list_is_400_and_nothing_is_logged()
    {
        var (planner, wo) = await ReleasedAsync(2);
        var serial = (await SerialNosAsync(wo.Id)).First();

        var response = await planner.PostAsJsonAsync($"/api/work-orders/{wo.Id}/labels/print",
            new PrintLabelsRequest([serial, "PCBA0000000000000"]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await LogCountAsync(wo.Id));
    }

    [Theory]
    [InlineData("qc01")]
    [InlineData("mt01")]
    public async Task Only_planner_supervisor_and_admin_print(string user)
    {
        var (_, wo) = await ReleasedAsync(1);
        var client = await api.LoginAsync(user);

        var response = await client.PostAsJsonAsync($"/api/work-orders/{wo.Id}/labels/print", new PrintLabelsRequest());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ---------- helpers ----------

    private async Task<CreateWorkOrderRequest> RequestAsync(int qty)
    {
        var pcbA = await api.WithDbAsync(db => db.Items.Where(i => i.Code == "PCB-A").Select(i => i.Id).SingleAsync());
        return new CreateWorkOrderRequest(pcbA, LineId: 1, qty, Start, Start.AddHours(4));
    }

    private async Task<(HttpClient Planner, WorkOrderDetailDto Wo)> ReleasedAsync(int qty)
    {
        var planner = await api.LoginAsync("planner01");
        var wo = await ReleaseWorkOrderTests.ReleaseOkAsync(planner, await WorkOrderApiTests.CreateAsync(planner, await RequestAsync(qty)));
        return (planner, wo);
    }

    private static async Task<PrintLabelsResultDto> PrintOkAsync(HttpClient client, WorkOrderDetailDto wo, PrintLabelsRequest request)
    {
        var response = await client.PostAsJsonAsync($"/api/work-orders/{wo.Id}/labels/print", request);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<PrintLabelsResultDto>())!;
    }

    private Task<List<string>> SerialNosAsync(int woId)
        => api.WithDbAsync(db => db.Serials.Where(s => s.WorkOrderId == woId).Select(s => s.SerialNo).ToListAsync());

    private Task<List<Domain.Planning.LabelPrintLog>> LogsAsync(int woId)
        => api.WithDbAsync(db => db.LabelPrintLogs.Where(l => l.WorkOrderId == woId).ToListAsync());

    private Task<int> LogCountAsync(int woId) => api.WithDbAsync(db => db.LabelPrintLogs.CountAsync(l => l.WorkOrderId == woId));
}
