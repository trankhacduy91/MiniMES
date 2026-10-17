using System.Net;
using MiniMes.Client.Core.Http;
using MiniMes.Client.Core.Navigation;
using MiniMes.Client.Core.Resources;
using MiniMes.Client.Core.ViewModels.MasterData;
using MiniMes.Contracts.Common;
using MiniMes.Contracts.MasterData;

namespace MiniMes.UnitTests.Client;

// BOM screen without a window: versions, lock after approve, save errors, tree, explode.
public class BomViewModelTests
{
    private static readonly ItemDto PcbA = new(1, "PCB-A", "Board A", "Finished", "pcs", true, false, false, true);
    private static readonly ItemDto Smt = new(2, "PCBA-SMT", "After SMT", "Semi", "pcs", false, false, false, true);
    private static readonly ItemDto Res = new(3, "RES-10K", "Resistor", "Raw", "pcs", false, true, false, true);

    private sealed class BomApi : FakeMasterDataApi
    {
        public List<BomSummaryDto> Versions { get; } =
        [
            new(11, 1, "PCB-A", 2, "Draft", null, null, 1),
            new(10, 1, "PCB-A", 1, "Approved", DateTime.UtcNow.AddDays(-5), null, 1),
        ];
        public SaveBomLinesRequest? Saved { get; private set; }
        public List<int> Approved { get; } = [];
        public decimal? ExplodedQty { get; private set; }
        public Exception? SaveFails { get; set; }

        public override Task<PagedResult<ItemDto>> GetItemsAsync(string? search, string? type, bool? isActive, int page, int pageSize, CancellationToken ct = default)
        {
            ItemDto[] all = [PcbA, Smt, Res];
            var items = all.Where(i => type is null || i.Type == type).ToList();
            return Task.FromResult(new PagedResult<ItemDto>(items, 1, pageSize, items.Count));
        }

        public override Task<IReadOnlyList<BomSummaryDto>> GetBomsAsync(int itemId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<BomSummaryDto>>(Versions);

        public override Task<BomDto> GetBomAsync(int id, CancellationToken ct = default)
            => Task.FromResult(new BomDto(id, 1, "PCB-A", 1, "Draft", null, null,
                [new BomLineDto(2, "PCBA-SMT", "After SMT", "pcs", 1m, 0.01m, 20)]));

        public override Task<BomTreeNodeDto> GetBomTreeAsync(int itemId, decimal qty, DateTime? asOfUtc, CancellationToken ct = default)
            => Task.FromResult(new BomTreeNodeDto(1, "PCB-A", "Board A", "Finished", "pcs", 1m, 0m, 0, 1m,
                [new BomTreeNodeDto(2, "PCBA-SMT", "After SMT", "Semi", "pcs", 1m, 0.01m, 20, 1.01m,
                    [new BomTreeNodeDto(3, "RES-10K", "Resistor", "Raw", "pcs", 12m, 0.02m, 10, 12.3624m, [])])]));

        public override Task<BomDto> SaveBomLinesAsync(int bomId, SaveBomLinesRequest request, CancellationToken ct = default)
        {
            Saved = request;
            return SaveFails is null
                ? Task.FromResult(new BomDto(bomId, 1, "PCB-A", 2, "Draft", null, null, []))
                : Task.FromException<BomDto>(SaveFails);
        }

        public override Task<BomDto> ApproveBomAsync(int bomId, ApproveVersionRequest request, CancellationToken ct = default)
        {
            Approved.Add(bomId);
            return Task.FromResult(new BomDto(bomId, 1, "PCB-A", 2, "Approved", DateTime.UtcNow, null, []));
        }

        public override Task<BomExplosionDto> ExplodeAsync(int itemId, decimal qty, DateTime? asOfUtc, CancellationToken ct = default)
        {
            ExplodedQty = qty;
            return Task.FromResult(new BomExplosionDto(itemId, "PCB-A", qty, DateTime.UtcNow,
                [new ExplosionLineDto(3, "RES-10K", "Resistor", "Raw", "pcs", 2472.48m, 2473m)]));
        }
    }

    private sealed class Dialogs(bool answer) : IDialogService
    {
        public bool Confirm(string message) => answer;
        public string? PickFileToOpen(string filter) => null;
        public string? PickFileToSave(string defaultFileName, string filter) => null;
    }

    private static async Task<(BomViewModel Vm, BomApi Api)> OpenPcbAAsync(bool confirm = true)
    {
        var api = new BomApi();
        var vm = new BomViewModel(api, new Dialogs(confirm));
        await vm.LoadAsync();
        vm.SelectedParent = vm.Parents.Single(p => p.Code == "PCB-A");
        await vm.Loading;
        return (vm, api);
    }

    [Fact]
    public async Task Only_semi_and_finished_items_can_be_parents_but_any_item_is_a_component()
    {
        var vm = new BomViewModel(new BomApi(), new Dialogs(true));

        await vm.LoadAsync();

        Assert.Equal(["PCB-A", "PCBA-SMT"], vm.Parents.Select(p => p.Code));
        Assert.Equal(3, vm.Components.Count);
    }

    [Fact]
    public async Task Choosing_an_item_loads_its_versions_lines_and_the_tree_in_force()
    {
        var (vm, _) = await OpenPcbAAsync();

        Assert.Equal(2, vm.Versions.Count);
        Assert.Equal(11, vm.SelectedVersion!.Id);                 // newest first
        Assert.Equal(2, Assert.Single(vm.Lines).ComponentItemId);
        var root = Assert.Single(vm.TreeRoots);
        Assert.Equal("RES-10K", root.Children[0].Children[0].Code); // 3 levels
        Assert.Equal("x12", root.Children[0].Children[0].QtyText);
        Assert.Equal("scrap 2%", root.Children[0].Children[0].ScrapText);
    }

    // Spec step 8 check: Approve is greyed out when the BOM is already Approved.
    [Fact]
    public async Task Approved_version_is_locked_on_screen()
    {
        var (vm, _) = await OpenPcbAAsync();

        Assert.True(vm.ApproveCommand.CanExecute(null));          // Draft with a line
        vm.SelectedVersion = vm.Versions.Single(v => v.Status == "Approved");
        await vm.Loading;

        Assert.False(vm.IsDraft);
        Assert.False(vm.ApproveCommand.CanExecute(null));
        Assert.False(vm.SaveLinesCommand.CanExecute(null));
        Assert.False(vm.AddLineCommand.CanExecute(null));
    }

    [Fact]
    public async Task Approve_asks_first()
    {
        var (vm, api) = await OpenPcbAAsync(confirm: false);

        await vm.ApproveCommand.ExecuteAsync(null);

        Assert.Empty(api.Approved);
    }

    [Fact]
    public async Task Approve_on_yes_calls_the_api_for_the_selected_draft()
    {
        var (vm, api) = await OpenPcbAAsync();

        await vm.ApproveCommand.ExecuteAsync(null);

        Assert.Equal([11], api.Approved);
    }

    [Fact]
    public async Task Save_sends_every_line_of_the_grid()
    {
        var (vm, api) = await OpenPcbAAsync();

        vm.AddLineCommand.Execute(null);
        vm.Lines[1].ComponentItemId = 3;
        vm.Lines[1].QtyPer = 12m;
        await vm.SaveLinesCommand.ExecuteAsync(null);

        Assert.Equal([(2, 1m), (3, 12m)], api.Saved!.Lines.Select(l => (l.ComponentItemId, l.QtyPer)));
        Assert.Equal(20, api.Saved.Lines[1].OperationSeq); // a new line starts at the previous line's step
    }

    [Fact]
    public async Task Cycle_refused_by_the_server_is_shown_as_the_page_error()
    {
        var (vm, api) = await OpenPcbAAsync();
        api.SaveFails = new ApiException(HttpStatusCode.BadRequest, "These lines would create a BOM cycle: PCB-A -> PCBA-SMT -> PCB-A.");

        await vm.SaveLinesCommand.ExecuteAsync(null);

        Assert.Contains("PCB-A -> PCBA-SMT -> PCB-A", vm.ErrorMessage);
    }

    [Theory]
    [InlineData("200", 200)]
    [InlineData("0,5", 0.5)]
    [InlineData(" 0.5 ", 0.5)]
    public async Task Explode_reads_the_quantity_with_comma_or_point(string text, decimal expected)
    {
        var (vm, api) = await OpenPcbAAsync();
        vm.ExplodeQty = text;

        await vm.ExplodeCommand.ExecuteAsync(null);

        Assert.Equal(expected, api.ExplodedQty);
        Assert.Equal(2473m, Assert.Single(vm.Explosion).RequiredQty);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("abc")]
    public async Task Bad_quantity_is_stopped_before_the_api(string text)
    {
        var (vm, api) = await OpenPcbAAsync();
        vm.ExplodeQty = text;

        await vm.ExplodeCommand.ExecuteAsync(null);

        Assert.Null(api.ExplodedQty);
        Assert.Equal(Strings.Bom_QtyInvalid, vm.ErrorMessage);
    }

    // Same regression as the routing editor: saving with an unchanged line count reloads an equal record.
    [Fact]
    public async Task After_save_the_lines_are_reloaded_even_if_the_version_looks_the_same()
    {
        var (vm, _) = await OpenPcbAAsync();

        await vm.SaveLinesCommand.ExecuteAsync(null);

        Assert.Single(vm.Lines);
    }
}
