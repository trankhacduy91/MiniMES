using System.Net;
using System.Net.Http.Json;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using MiniMes.Contracts.MasterData;
using MiniMes.Domain.MasterData;
using MiniMes.Domain.MasterData.Equipment;
using MiniMes.Domain.MasterData.Items;
using MiniMes.IntegrationTests.Infrastructure;

namespace MiniMes.IntegrationTests.MasterData;

// Spec M1 step 8b with the real sample file docs/samples/import-PCB-B.xlsx.
[Collection(ApiCollection.Name)]
public class ImportTests(ApiFixture api)
{
    // (a) + (e): valid file -> dry run clean -> commit -> importing it again is refused, nothing duplicated.
    [Fact]
    public async Task Pcb_b_sample_imports_once_as_drafts_and_a_second_import_is_refused()
    {
        var admin = await ReadyAsync();
        var sample = SampleBytes();

        var dry = await ImportAsync(admin, sample, dryRun: true);
        var commit = await ImportAsync(admin, sample, dryRun: false);
        var again = await ImportAsync(admin, sample, dryRun: false);

        Assert.Empty(dry.Errors);
        Assert.Equal((false, 5, 2, 1), (dry.Committed, dry.ItemCount, dry.BomCount, dry.RoutingCount));
        Assert.True(commit.Committed);
        var saved = await api.WithDbAsync(async db => new
        {
            Items = await db.Items.CountAsync(i => i.Code == "PCB-B" || i.Code == "PCBB-SMT" || i.Code == "IC-MCU-B"),
            Boms = await db.Boms.Where(b => db.Items.Any(i => i.Id == b.ItemId && (i.Code == "PCB-B" || i.Code == "PCBB-SMT")))
                .Select(b => new { b.Status, Lines = b.Lines.Count }).ToListAsync(),
            Routing = await db.Routings.Where(r => db.Items.Any(i => i.Id == r.ItemId && i.Code == "PCB-B"))
                .Select(r => new { r.Status, Steps = r.Steps.Count }).SingleAsync(),
        });
        Assert.Equal(3, saved.Items);
        Assert.Equal([(VersionStatus.Draft, 2), (VersionStatus.Draft, 5)], saved.Boms.Select(b => (b.Status, b.Lines)).Order());
        Assert.Equal((VersionStatus.Draft, 4), (saved.Routing.Status, saved.Routing.Steps));

        Assert.False(again.Committed);
        Assert.Contains(again.Errors, e => e is { Sheet: "Items", Row: 2, Column: "Code" } && e.Message == "Code 'PCB-B' already exists.");
        Assert.Equal(1, await api.WithDbAsync(db => db.Items.CountAsync(i => i.Code == "PCB-B")));
    }

    // (b): one bad cell -> exact location in the dry run; the commit is refused and writes nothing at all.
    [Fact]
    public async Task Bad_uom_is_reported_at_items_row_5_and_nothing_is_written()
    {
        var admin = await ReadyAsync();
        var file = Modify(wb => wb.Worksheet("Items").Cell(5, 4).Value = "xyz");
        var before = await CountRowsAsync();

        var dry = await ImportAsync(admin, file, dryRun: true);
        var commit = await ImportAsync(admin, file, dryRun: false);

        Assert.Contains(dry.Errors, e => e is { Sheet: "Items", Row: 5, Column: "Uom" });
        Assert.False(commit.Committed);
        Assert.Contains(commit.Errors, e => e is { Sheet: "Items", Row: 5, Column: "Uom" });
        Assert.Equal(before, await CountRowsAsync());
    }

    // (c): a loop inside the file is found on the combined graph and spelled out.
    [Fact]
    public async Task Bom_cycle_in_the_file_is_reported_with_its_path()
    {
        var admin = await ReadyAsync();
        var file = Modify(wb =>
        {
            var ws = wb.Worksheet("BomLines");
            ws.Cell(9, 1).Value = "PCBB-SMT";
            ws.Cell(9, 2).Value = "PCB-B";
            ws.Cell(9, 3).Value = 1;
            ws.Cell(9, 4).Value = 0;
            ws.Cell(9, 5).Value = 10;
        });

        var result = await ImportAsync(admin, file, dryRun: true);

        Assert.Contains(result.Errors, e => e.Sheet == "BomLines" && e.Message.Contains("PCB-B -> PCBB-SMT -> PCB-B"));
    }

    [Fact]
    public async Task Unknown_component_and_work_center_are_reported_on_their_cells()
    {
        var admin = await ReadyAsync();
        var file = Modify(wb =>
        {
            wb.Worksheet("BomLines").Cell(3, 2).Value = "NOT-THERE";
            wb.Worksheet("RoutingSteps").Cell(2, 4).Value = "WC-NOPE";
        });

        var result = await ImportAsync(admin, file, dryRun: true);

        Assert.Contains(result.Errors, e => e is { Sheet: "BomLines", Row: 3, Column: "ComponentCode" });
        Assert.Contains(result.Errors, e => e is { Sheet: "RoutingSteps", Row: 2, Column: "WorkCenterCode" });
    }

    [Fact]
    public async Task Template_downloads_and_import_needs_admin()
    {
        var admin = await api.LoginAsync("admin");
        var op = await api.LoginOperatorAsync();

        var template = await admin.GetAsync("/api/import/template");
        var asOperator = await op.PostAsync("/api/import/master-data", Upload(SampleBytes()));

        Assert.Equal(HttpStatusCode.OK, template.StatusCode);
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", template.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.Forbidden, asOperator.StatusCode);
    }

    [Fact]
    public async Task Not_an_excel_file_is_a_clear_error_not_a_500()
    {
        var admin = await api.LoginAsync("admin");

        var result = await ImportAsync(admin, "not a workbook"u8.ToArray(), dryRun: true);

        Assert.Equal("File", Assert.Single(result.Errors).Sheet);
    }

    // ---------- helpers ----------

    // The sample refers to PCB-A components and Line 1 work centers (seeded in step 9). Create any that
    // are missing so the test does not depend on the seeder; codes are fixed on purpose.
    private async Task<HttpClient> ReadyAsync()
    {
        await api.WithDbAsync(async db =>
        {
            foreach (var (code, uom) in new[] { ("RES-10K-0402", Units.Pieces), ("CAP-100N-0402", Units.Pieces), ("SOLDER-PASTE", Units.Kilogram) })
                if (!await db.Items.AnyAsync(i => i.Code == code))
                    db.Items.Add(Item.Create(code, code, ItemType.Raw, uom, false, true));
            var lineId = await db.Lines.OrderBy(l => l.Id).Select(l => l.Id).FirstAsync();
            foreach (var (code, type) in new[] { ("WC-SMT", "SMT"), ("WC-ASSY", "ASSY"), ("WC-TEST", "TEST"), ("WC-PACK", "PACK") })
                if (!await db.WorkCenters.AnyAsync(w => w.Code == code))
                    db.WorkCenters.Add(new WorkCenter(lineId, code, code, type));
            return await db.SaveChangesAsync();
        });
        return await api.LoginAsync("admin");
    }

    private static byte[] SampleBytes() => File.ReadAllBytes(Path.Combine(RepoRoot(), "docs", "samples", "import-PCB-B.xlsx"));

    private static byte[] Modify(Action<XLWorkbook> change)
    {
        using var wb = new XLWorkbook(new MemoryStream(SampleBytes()));
        change(wb);
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    private static MultipartFormDataContent Upload(byte[] bytes)
        => new() { { new ByteArrayContent(bytes), "file", "import.xlsx" } };

    private static async Task<ImportResultDto> ImportAsync(HttpClient admin, byte[] file, bool dryRun)
    {
        var response = await admin.PostAsync($"/api/import/master-data?dryRun={dryRun}", Upload(file));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ImportResultDto>())!;
    }

    private Task<(int, int, int)> CountRowsAsync()
        => api.WithDbAsync(async db => (await db.Items.CountAsync(), await db.Boms.CountAsync(), await db.Routings.CountAsync()));

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MiniMes.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("MiniMes.slnx not found.");
    }
}
