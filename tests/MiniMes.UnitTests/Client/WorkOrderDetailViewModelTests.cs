using System.ComponentModel;
using System.Net;
using MiniMes.Client.Core.Http;
using MiniMes.Client.Core.Navigation;
using MiniMes.Client.Core.Resources;
using MiniMes.Client.Core.ViewModels.Planning;
using MiniMes.Contracts.Common;
using MiniMes.Contracts.Planning;

namespace MiniMes.UnitTests.Client;

// Spec M2 step 8, session 2: the detail screen enables buttons from allowedActions, asks a reason where the
// rules need one, sends the loaded RowVersion, handles 409, previews and prints labels.
public class WorkOrderDetailViewModelTests
{
    private static readonly TimeZoneInfo Vietnam = TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh");
    private sealed class PlantClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 10, 17, 3, 0, 0, TimeSpan.Zero);
        public override TimeZoneInfo LocalTimeZone => Vietnam;
    }

    private static WorkOrderDetailDto Wo(string status, string[] actions, int good = 0, string rowVersion = "AAAAAAAAB9E=", int? bom = 1) => new(
        7, "WO-2610-00007", 1, "PCB-A", "Controller board A", 1, "L1", 200,
        new DateTime(2026, 11, 2, 1, 0, 0, DateTimeKind.Utc), new DateTime(2026, 11, 2, 5, 0, 0, DateTimeKind.Utc),
        status, 5, true, bom is null ? null : "Pending", null, bom, bom, null, null, null, null, null, null, null, good,
        bom is null ? 0 : 200, actions, rowVersion,
        [new WorkOrderMaterialDto(4, "RES-10K-0402", "Resistor", "pcs", 10, 12.3624m, 2472.48m, 2473m, 0, 0)],
        [new WorkOrderOperationDto(10, "SMT", 1, "WC-SMT", 30m, false, true, null, good, 0)]);

    private static readonly string[] Draft = ["Edit", "Release", "Cancel", "ChangePriority"];
    private static readonly string[] Running = ["Hold", "Complete", "Cancel", "ChangePriority", "PrintLabels"];

    private sealed class Api(WorkOrderDetailDto start) : FakeWorkOrderApi
    {
        public WorkOrderDetailDto Current { get; set; } = start;
        public List<(string Action, WorkOrderTransitionRequest Request)> Transitions { get; } = [];
        public int SerialLoads { get; private set; }
        public PreviewLabelRequest? Previewed { get; private set; }
        public PrintLabelsRequest? Printed { get; private set; }
        public Exception? Fail { get; set; }
        public WorkOrderDetailDto? After { get; set; }

        public override Task<WorkOrderDetailDto> GetWorkOrderAsync(int id, CancellationToken ct = default) => Task.FromResult(Current);

        public override Task<PagedResult<SerialDto>> GetSerialsAsync(int id, int page, int pageSize, CancellationToken ct = default)
        {
            SerialLoads++;
            return Task.FromResult(new PagedResult<SerialDto>(
                [new SerialDto(1, "PCBA2610170000049", "Created", 0), new SerialDto(2, "PCBA2610170000058", "Created", 0)], page, pageSize, 200));
        }

        public override Task<WorkOrderDetailDto> TransitionAsync(int id, string action, WorkOrderTransitionRequest request, CancellationToken ct = default)
        {
            Transitions.Add((action, request));
            if (Fail is not null) return Task.FromException<WorkOrderDetailDto>(Fail);
            Current = After ?? Current;
            return Task.FromResult(Current);
        }

        public override Task<byte[]> PreviewLabelAsync(int id, PreviewLabelRequest request, CancellationToken ct = default)
        {
            Previewed = request;
            return Task.FromResult<byte[]>([0x89, 0x50]);
        }

        public override Task<PrintLabelsResultDto> PrintLabelsAsync(int id, PrintLabelsRequest request, CancellationToken ct = default)
        {
            Printed = request;
            return Task.FromResult(new PrintLabelsResultDto(request.SerialNos?.Count ?? 200, request.Reason is null ? 0 : 1, "^XA^FDtest^FS^XZ"));
        }
    }

    private sealed class Dialogs : IDialogService
    {
        public bool ConfirmAnswer { get; set; } = true;
        public string? TextAnswer { get; set; } = "Suspect IC lot moisture";
        public string? SavePath { get; set; }
        public List<string> Prompts { get; } = [];

        public bool Confirm(string message) { Prompts.Add(message); return ConfirmAnswer; }
        public string? PickFileToOpen(string filter) => null;
        public string? PickFileToSave(string defaultFileName, string filter) => SavePath;
        public string? AskText(string title, string prompt) { Prompts.Add(prompt); return TextAnswer; }
    }

    private sealed class Pages : IPageNavigator
    {
        public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
        public object? CurrentPage => null;
        public Task ShowAsync<TPage>(Func<TPage, Task>? init = null) where TPage : class => Task.CompletedTask;
        public void Clear() { }
    }

    private static async Task<(WorkOrderDetailViewModel Vm, Api Api, Dialogs Dialogs)> OpenAsync(WorkOrderDetailDto wo)
    {
        var (api, dialogs) = (new Api(wo), new Dialogs());
        var vm = new WorkOrderDetailViewModel(api, new Pages(), dialogs, new PlantClock());
        await vm.InitAsync(wo.Id);
        return (vm, api, dialogs);
    }

    [Fact]
    public async Task Draft_shows_only_its_allowed_buttons_and_plant_time()
    {
        var (vm, _, _) = await OpenAsync(Wo("Draft", Draft, bom: null));

        Assert.True(vm.ReleaseCommand.CanExecute(null));
        Assert.True(vm.CancelCommand.CanExecute(null));
        Assert.False(vm.HoldCommand.CanExecute(null));
        Assert.False(vm.CompleteCommand.CanExecute(null));
        Assert.False(vm.PrintLabelsCommand.CanExecute(null));
        Assert.Equal("2026-11-02 08:00 - 2026-11-02 12:00", vm.PlannedWindow);
        Assert.Equal(Strings.WorkOrderDetail_NotReleased, vm.Versions);
    }

    [Fact]
    public async Task Release_asks_first_then_sends_the_loaded_row_version_and_reloads_serials()
    {
        var (vm, api, dialogs) = await OpenAsync(Wo("Draft", Draft, bom: null));
        api.After = Wo("Released", ["Start", "Cancel", "ChangePriority", "PrintLabels"], rowVersion: "AAAAAAAAB9I=");
        var serialLoads = api.SerialLoads;

        dialogs.ConfirmAnswer = false;
        await vm.ReleaseCommand.ExecuteAsync(null);
        Assert.Empty(api.Transitions);                     // "No" = nothing sent

        dialogs.ConfirmAnswer = true;
        await vm.ReleaseCommand.ExecuteAsync(null);

        Assert.Equal(("release", new WorkOrderTransitionRequest("AAAAAAAAB9E=", null)), api.Transitions.Single());
        Assert.Equal("Released", vm.Detail!.Status);
        Assert.False(vm.ReleaseCommand.CanExecute(null));   // re-evaluated from the new allowedActions
        Assert.True(vm.PrintLabelsCommand.CanExecute(null));
        Assert.Equal(serialLoads + 1, api.SerialLoads);
    }

    [Fact]
    public async Task Hold_needs_a_reason_and_cancelling_the_prompt_sends_nothing()
    {
        var (vm, api, dialogs) = await OpenAsync(Wo("InProgress", Running));

        dialogs.TextAnswer = null;
        await vm.HoldCommand.ExecuteAsync(null);
        Assert.Empty(api.Transitions);

        dialogs.TextAnswer = "Suspect IC lot moisture";
        await vm.HoldCommand.ExecuteAsync(null);
        Assert.Equal(("hold", new WorkOrderTransitionRequest("AAAAAAAAB9E=", "Suspect IC lot moisture")), api.Transitions.Single());
    }

    [Fact]
    public async Task Complete_below_plan_asks_why_and_at_plan_only_confirms()
    {
        var (shortVm, shortApi, shortDialogs) = await OpenAsync(Wo("InProgress", Running, good: 197));
        await shortVm.CompleteCommand.ExecuteAsync(null);
        Assert.Contains("197 of 200", shortDialogs.Prompts.Single());
        Assert.Equal("Suspect IC lot moisture", shortApi.Transitions.Single().Request.Reason);

        var (fullVm, fullApi, fullDialogs) = await OpenAsync(Wo("InProgress", Running, good: 200));
        await fullVm.CompleteCommand.ExecuteAsync(null);
        Assert.Contains("WO-2610-00007", fullDialogs.Prompts.Single());
        Assert.Null(fullApi.Transitions.Single().Request.Reason);
    }

    // DoD: a 409 shows the English message from Strings.resx and the screen now shows the current order.
    [Fact]
    public async Task Conflict_reloads_the_order_and_explains()
    {
        var (vm, api, _) = await OpenAsync(Wo("Draft", Draft, bom: null));
        api.Fail = new ApiException(HttpStatusCode.Conflict, "changed");
        api.Current = Wo("Released", ["Start", "Cancel", "ChangePriority", "PrintLabels"], rowVersion: "AAAAAAAAB9I=");

        await vm.ReleaseCommand.ExecuteAsync(null);

        Assert.Equal(Strings.WorkOrder_Conflict, vm.ErrorMessage);
        Assert.Equal(("Released", "AAAAAAAAB9I="), (vm.Detail!.Status, vm.Detail.RowVersion));
        Assert.False(vm.ReleaseCommand.CanExecute(null));
    }

    // The other planner's Release was already committed: the server says 422, and this stale screen catches up.
    [Fact]
    public async Task Second_release_from_a_stale_screen_shows_the_reason_and_the_current_state()
    {
        var (vm, api, _) = await OpenAsync(Wo("Draft", Draft, bom: null));
        api.Fail = new ApiException(HttpStatusCode.UnprocessableEntity, "Work order WO-2610-00007 is Released; Release is not allowed.");
        api.Current = Wo("Released", ["Start", "Cancel", "ChangePriority", "PrintLabels"]);

        await vm.ReleaseCommand.ExecuteAsync(null);

        Assert.Contains("is Released", vm.ErrorMessage);
        Assert.Equal("Released", vm.Detail!.Status);
        Assert.False(vm.ReleaseCommand.CanExecute(null));
    }

    [Fact]
    public async Task Wrong_status_or_empty_reason_is_shown_on_the_page()
    {
        var (vm, api, _) = await OpenAsync(Wo("InProgress", Running));

        api.Fail = new ApiException(HttpStatusCode.UnprocessableEntity, "Work order WO-2610-00007 is OnHold; Hold is not allowed.");
        await vm.HoldCommand.ExecuteAsync(null);
        Assert.Contains("not allowed", vm.ErrorMessage);

        api.Fail = new ApiException(HttpStatusCode.BadRequest, "Validation failed", null,
            new Dictionary<string, string[]> { ["Reason"] = ["'Reason' must not be empty."] });
        await vm.CancelCommand.ExecuteAsync(null);
        Assert.Equal("'Reason' must not be empty.", vm.ErrorMessage);
    }

    // ---------- labels ----------

    [Fact]
    public async Task Preview_uses_the_selected_serial_and_shows_the_png()
    {
        var (vm, api, _) = await OpenAsync(Wo("Released", ["Start", "Cancel", "ChangePriority", "PrintLabels"]));
        vm.SelectedSerial = vm.Serials[1];

        await vm.PreviewLabelCommand.ExecuteAsync(null);

        Assert.Equal(new PreviewLabelRequest("PCBA2610170000058"), api.Previewed);
        Assert.Equal([0x89, 0x50], vm.LabelImage);
    }

    [Fact]
    public async Task Print_all_saves_the_zpl_file_and_reports()
    {
        var (vm, api, dialogs) = await OpenAsync(Wo("Released", ["Start", "Cancel", "ChangePriority", "PrintLabels"]));
        var path = Path.Combine(Path.GetTempPath(), $"labels-{Guid.NewGuid():N}.zpl");
        try
        {
            dialogs.SavePath = null;
            await vm.PrintLabelsCommand.ExecuteAsync(null);
            Assert.Null(api.Printed);                          // no file chosen = nothing printed or logged

            dialogs.SavePath = path;
            await vm.PrintLabelsCommand.ExecuteAsync(null);

            Assert.Equal(new PrintLabelsRequest(null, null), api.Printed);
            Assert.Equal("^XA^FDtest^FS^XZ", await File.ReadAllTextAsync(path));
            Assert.Contains("200", vm.InfoMessage);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Reprint_selected_asks_why_and_sends_one_serial_with_the_reason()
    {
        var (vm, api, dialogs) = await OpenAsync(Wo("Released", ["Start", "Cancel", "ChangePriority", "PrintLabels"]));
        Assert.False(vm.ReprintSelectedCommand.CanExecute(null));   // nothing selected yet
        vm.SelectedSerial = vm.Serials[0];
        dialogs.TextAnswer = "Label burnt in reflow";
        dialogs.SavePath = Path.Combine(Path.GetTempPath(), $"reprint-{Guid.NewGuid():N}.zpl");
        try
        {
            await vm.ReprintSelectedCommand.ExecuteAsync(null);

            Assert.Equal(["PCBA2610170000049"], api.Printed!.SerialNos);
            Assert.Equal("Label burnt in reflow", api.Printed.Reason);
        }
        finally { File.Delete(dialogs.SavePath); }
    }
}
