using System.Net;
using MiniMes.Client.Core.Api;
using MiniMes.Client.Core.Auth;
using MiniMes.Client.Core.Http;
using MiniMes.Client.Core.Navigation;
using MiniMes.Client.Core.Resources;
using MiniMes.Client.Core.ViewModels.Quality;
using MiniMes.Contracts.Auth;
using MiniMes.Contracts.Quality;

namespace MiniMes.UnitTests.Client;

internal sealed class FakeFaiApi : IFaiApi
{
    public FaiWorksheetDto Sheet { get; set; } = Worksheet();
    public List<RecordFaiRequest> Recorded { get; } = [];
    public ApiException? Fail { get; set; }
    public int WorksheetReads { get; private set; }

    public static FaiCheckDto Auto(string code, bool passed, string? detail = null) => new(code, code, "Auto", passed, detail);
    public static FaiCheckDto Manual(string code) => new(code, code, "Manual", null, null);

    public static FaiWorksheetDto Worksheet(bool ready = true, bool bomGreen = true, string? fai = "Pending") => new(
        7, "WO-2611-00007", "PCB-A", "L1", fai, "PCBA-1", "InProcess", ready, ready ? null : "Sample PCBA-1 has not passed TEST yet.", 1,
        [Auto("BomMatch", bomGreen, bomGreen ? null : "not in the work order BOM: RES-1K."), Auto("TestPassed", true), Auto("MeasurementsInSpec", true),
         Manual("VISUAL"), Manual("LABEL")],
        bomGreen, [], "AAAAAAAAB9E=");

    public Task<IReadOnlyList<FaiQueueItemDto>> GetQueueAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<FaiQueueItemDto>>(
            [new FaiQueueItemDto(Sheet.WorkOrderId, Sheet.WoNo, "PCB-A", "L1", "InProgress", "PCBA-1", "InProcess", Sheet.SampleReady)]);

    public Task<FaiWorksheetDto> GetWorksheetAsync(int workOrderId, CancellationToken ct = default)
    {
        WorksheetReads++;
        return Task.FromResult(Sheet);
    }

    public Task<FaiWorksheetDto> RecordAsync(int workOrderId, RecordFaiRequest request, CancellationToken ct = default)
    {
        Recorded.Add(request);
        if (Fail is { } ex) return Task.FromException<FaiWorksheetDto>(ex);
        Sheet = Sheet with
        {
            FaiStatus = request.Result == "Pass" ? "Passed" : "Pending",
            SampleReady = false,
            History = [new FaiRecordDto(1, "PCBA-1", 1, request.Result, true, request.DefectCode, request.Note, "qc01", DateTime.UtcNow, [])],
        };
        return Task.FromResult(Sheet);
    }
}

// [v2] Spec M7 step 8b: the FAI screen. Pass only with green automatic checks and every manual item ticked (Q17);
// the API (fake here) checks it again.
public class FaiViewModelTests
{
    private readonly FakeFaiApi _api = new();

    private async Task<FaiViewModel> ShowingAsync(string role = "QC", IDialogService? dialogs = null)
    {
        var tokens = new TokenStore();
        tokens.Set(new LoginResponse("t", DateTime.UtcNow.AddHours(1), "qc01", "qc01", [role], null));
        var vm = new FaiViewModel(_api, new DefectCodesApi(), tokens, dialogs ?? new AnswerDialogs());
        await vm.LoadAsync();
        vm.SelectedRow = vm.Queue.Single();
        await Task.Yield();
        return vm;
    }

    private static void TickAll(FaiViewModel vm)
    {
        foreach (var i in vm.Items.Where(i => i.IsManual)) i.IsChecked = true;
    }

    [Fact]
    public async Task Pass_waits_for_every_manual_item_then_sends_the_ticks()
    {
        var vm = await ShowingAsync();
        Assert.Equal(["OK", "OK", "OK"], vm.Items.Where(i => i.IsAuto).Select(i => i.StateText));
        Assert.False(vm.PassCommand.CanExecute(null));
        Assert.Equal(Strings.Fai_TickAll, vm.PassHint);
        Assert.True(vm.FailCommand.CanExecute(null));   // QC may fail before ticking anything

        TickAll(vm);
        Assert.True(vm.PassCommand.CanExecute(null));
        Assert.Equal(string.Empty, vm.PassHint);
        vm.Note = "  first board fine ";
        await vm.PassCommand.ExecuteAsync(null);

        var sent = Assert.Single(_api.Recorded);
        Assert.Equal(("Pass", "first board fine", (string?)null, "AAAAAAAAB9E="), (sent.Result, sent.Note, sent.DefectCode, sent.RowVersion));
        Assert.Equal(["VISUAL", "LABEL"], sent.CheckedItems!);
        Assert.Equal("Passed", vm.Sheet!.FaiStatus);
        Assert.Single(vm.History);
        Assert.Contains("WO-2611-00007", vm.InfoMessage);
    }

    [Fact]
    public async Task A_red_automatic_check_keeps_pass_off_with_the_reason_on_screen()
    {
        _api.Sheet = FakeFaiApi.Worksheet(bomGreen: false);
        var vm = await ShowingAsync();

        TickAll(vm);

        var bom = vm.Items.Single(i => i.Code == "BomMatch");
        Assert.Equal(("NG", "not in the work order BOM: RES-1K."), (bom.StateText, bom.Detail));
        Assert.False(vm.PassCommand.CanExecute(null));
        Assert.Equal(Strings.Fai_AutoRed, vm.PassHint);
    }

    [Fact]
    public async Task Sample_not_through_test_shows_the_server_reason_and_nothing_can_be_recorded()
    {
        _api.Sheet = FakeFaiApi.Worksheet(ready: false);
        var vm = await ShowingAsync();
        TickAll(vm);

        Assert.False(vm.PassCommand.CanExecute(null));
        Assert.False(vm.FailCommand.CanExecute(null));
        Assert.Equal("Sample PCBA-1 has not passed TEST yet.", vm.PassHint);
    }

    [Fact]
    public async Task A_supervisor_reads_the_worksheet_but_cannot_record()
    {
        var vm = await ShowingAsync(role: "Supervisor");
        TickAll(vm);

        Assert.False(vm.PassCommand.CanExecute(null));
        Assert.False(vm.FailCommand.CanExecute(null));
        Assert.Equal(Strings.Fai_OnlyQc, vm.PassHint);
    }

    // Fail sends a real board to Repair: asked first; No sends nothing. Yes sends the chosen defect code.
    [Fact]
    public async Task Fail_asks_first_and_sends_the_defect_code()
    {
        var no = await ShowingAsync(dialogs: new AnswerDialogs(confirm: false));
        await no.FailCommand.ExecuteAsync(null);
        Assert.Empty(_api.Recorded);

        var dialogs = new AnswerDialogs();
        var vm = await ShowingAsync(dialogs: dialogs);
        vm.DefectCode = "MEAS-OOS";
        await vm.FailCommand.ExecuteAsync(null);

        Assert.Contains("PCBA-1", Assert.Single(dialogs.Confirms));
        Assert.Equal(("Fail", "MEAS-OOS"), (_api.Recorded[0].Result, _api.Recorded[0].DefectCode));
        Assert.Equal("Pending", vm.Sheet!.FaiStatus);
    }

    // Another QC judged meanwhile: 409 -> the error and the worksheet as it is now.
    [Fact]
    public async Task A_409_reloads_the_worksheet()
    {
        var vm = await ShowingAsync();
        TickAll(vm);
        _api.Fail = new ApiException(HttpStatusCode.Conflict, "Work order WO-2611-00007 changed since the worksheet was opened.");
        var reads = _api.WorksheetReads;

        await vm.PassCommand.ExecuteAsync(null);

        Assert.Contains("changed", vm.ErrorMessage);
        Assert.Equal(reads + 1, _api.WorksheetReads);
    }
}
