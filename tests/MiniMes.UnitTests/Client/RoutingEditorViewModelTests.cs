using MiniMes.Client.Core.Navigation;
using MiniMes.Client.Core.ViewModels.MasterData;
using MiniMes.Contracts.Common;
using MiniMes.Contracts.MasterData;

namespace MiniMes.UnitTests.Client;

// Routing editor without a window: Up/Down renumbering (spec M1 2.7), lock after approve, what Save sends.
public class RoutingEditorViewModelTests
{
    private static readonly ItemDto PcbA = new(1, "PCB-A", "Board A", "Finished", "pcs", true, false, false, true);

    private sealed class RoutingApi : FakeMasterDataApi
    {
        public string DraftStatus { get; set; } = "Draft";
        public SaveRoutingStepsRequest? Saved { get; private set; }

        public override Task<PagedResult<ItemDto>> GetItemsAsync(string? search, string? type, bool? isActive, int page, int pageSize, CancellationToken ct = default)
        {
            List<ItemDto> items = type == "Finished" ? [PcbA] : [];
            return Task.FromResult(new PagedResult<ItemDto>(items, 1, pageSize, items.Count));
        }

        public override Task<IReadOnlyList<WorkCenterDto>> GetWorkCentersAsync(bool? isActive = null, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<WorkCenterDto>>([new(7, 1, "WC-SMT", "SMT", "SMT", true), new(8, 1, "WC-TEST", "Test", "TEST", true)]);

        public override Task<IReadOnlyList<RoutingSummaryDto>> GetRoutingsAsync(int itemId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<RoutingSummaryDto>>([new(21, 1, "PCB-A", 2, DraftStatus, null, null, 4)]);

        // Returned out of order on purpose: the screen must sort by Seq.
        public override Task<RoutingDto> GetRoutingAsync(int id, CancellationToken ct = default)
            => Task.FromResult(new RoutingDto(id, 1, "PCB-A", 2, DraftStatus, null, null,
            [
                new(30, "TEST", 8, "WC-TEST", 45m, true, true, "/sop/PCB-A/TEST-v1.pdf"),
                new(10, "SMT", 7, "WC-SMT", 30m, false, true, null),
                new(40, "PACK", 7, "WC-SMT", 20m, false, true, null),
                new(20, "ASSY", 7, "WC-SMT", 60m, false, true, null),
            ]));

        public override Task<RoutingDto> SaveRoutingStepsAsync(int routingId, SaveRoutingStepsRequest request, CancellationToken ct = default)
        {
            Saved = request;
            return Task.FromResult(new RoutingDto(routingId, 1, "PCB-A", 2, "Draft", null, null, []));
        }
    }

    private sealed class Yes : IDialogService
    {
        public bool Confirm(string message) => true;
        public string? PickFileToOpen(string filter) => null;
        public string? PickFileToSave(string defaultFileName, string filter) => null;
        public string? AskText(string title, string prompt) => null;
    }

    private static async Task<(RoutingEditorViewModel Vm, RoutingApi Api)> OpenAsync(string status = "Draft")
    {
        var api = new RoutingApi { DraftStatus = status };
        var vm = new RoutingEditorViewModel(api, new Yes());
        await vm.LoadAsync();
        vm.SelectedParent = vm.Parents.Single();
        await vm.Loading;
        return (vm, api);
    }

    private static string Order(RoutingEditorViewModel vm) => string.Join(",", vm.Steps.Select(s => $"{s.Seq}:{s.OperationCode}"));

    [Fact]
    public async Task Steps_are_shown_in_seq_order()
    {
        var (vm, _) = await OpenAsync();

        Assert.Equal("10:SMT,20:ASSY,30:TEST,40:PACK", Order(vm));
        Assert.Equal("/sop/PCB-A/TEST-v1.pdf", vm.Steps[2].SopUrl);
    }

    // Spec step 8 check: reorder, Save, and Seq is 10,20,30,40 again in the new order.
    [Fact]
    public async Task Moving_a_step_renumbers_seq_and_save_sends_the_new_order()
    {
        var (vm, api) = await OpenAsync();

        vm.SelectedStep = vm.Steps.Single(s => s.OperationCode == "PACK");
        vm.MoveUpCommand.Execute(null);                    // PACK before TEST
        Assert.Equal("10:SMT,20:ASSY,30:PACK,40:TEST", Order(vm));
        await vm.SaveStepsCommand.ExecuteAsync(null);

        Assert.Equal([(10, "SMT"), (20, "ASSY"), (30, "PACK"), (40, "TEST")], api.Saved!.Steps.Select(s => (s.Seq, s.OperationCode)));
    }

    // Regression: the reloaded version is an equal record, so without a forced change the grid stayed empty.
    [Fact]
    public async Task After_save_the_steps_are_reloaded_from_the_server()
    {
        var (vm, _) = await OpenAsync();

        await vm.SaveStepsCommand.ExecuteAsync(null);

        Assert.Equal(4, vm.Steps.Count);
    }

    [Fact]
    public async Task Up_is_off_on_the_first_row_and_down_on_the_last_even_after_a_move()
    {
        var (vm, _) = await OpenAsync();

        vm.SelectedStep = vm.Steps[0];
        Assert.False(vm.MoveUpCommand.CanExecute(null));
        Assert.True(vm.MoveDownCommand.CanExecute(null));

        vm.SelectedStep = vm.Steps[1];
        vm.MoveUpCommand.Execute(null);                    // the same row is now first
        Assert.False(vm.MoveUpCommand.CanExecute(null));

        vm.SelectedStep = vm.Steps[^1];
        Assert.False(vm.MoveDownCommand.CanExecute(null));
    }

    [Fact]
    public async Task New_step_goes_after_the_highest_seq_and_inputs_are_cleaned_up()
    {
        var (vm, api) = await OpenAsync();

        vm.AddStepCommand.Execute(null);
        vm.SelectedStep!.OperationCode = " glue ";
        vm.SelectedStep.SopUrl = "   ";
        await vm.SaveStepsCommand.ExecuteAsync(null);

        var added = api.Saved!.Steps[^1];
        Assert.Equal((50, "GLUE", 7, (string?)null), (added.Seq, added.OperationCode, added.WorkCenterId, added.SopUrl));
    }

    [Fact]
    public async Task Approved_routing_cannot_be_edited_or_reordered()
    {
        var (vm, _) = await OpenAsync(status: "Approved");
        vm.SelectedStep = vm.Steps[1];

        Assert.False(vm.IsDraft);
        Assert.False(vm.MoveUpCommand.CanExecute(null));
        Assert.False(vm.AddStepCommand.CanExecute(null));
        Assert.False(vm.SaveStepsCommand.CanExecute(null));
        Assert.False(vm.ApproveCommand.CanExecute(null));
    }
}
