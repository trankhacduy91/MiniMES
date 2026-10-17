using System.ComponentModel;
using System.Net;
using MiniMes.Client.Core.Api;
using MiniMes.Client.Core.Http;
using MiniMes.Client.Core.Navigation;
using MiniMes.Client.Core.Resources;
using MiniMes.Client.Core.ViewModels.Planning;
using MiniMes.Contracts.Common;
using MiniMes.Contracts.Factory;
using MiniMes.Contracts.MasterData;
using MiniMes.Contracts.Planning;

namespace MiniMes.UnitTests.Client;

// Spec M2 step 8, session 1: work order list (filters, plant time, priority) and the create form with preview.
public class WorkOrderViewModelTests
{
    // The PC runs in plant time (Vietnam, UTC+7); "now" = 17 Oct 2026 10:00 in the plant.
    private static readonly TimeZoneInfo Vietnam = TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh");
    private sealed class PlantClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 10, 17, 3, 0, 0, TimeSpan.Zero);
        public override TimeZoneInfo LocalTimeZone => Vietnam;
    }

    private static WorkOrderSummaryDto Summary(int id, string status = "Released", int priority = 5) => new(
        id, $"WO-2610-0000{id}", 1, "PCB-A", 1, "L1", 200, 0, new DateTime(2026, 11, 2, 1, 0, 0, DateTimeKind.Utc),
        new DateTime(2026, 11, 2, 5, 0, 0, DateTimeKind.Utc), status, priority, "Pending");

    private static WorkOrderDetailDto Detail(int id, string rowVersion = "AAAAAAAAB9E=") => new(
        id, $"WO-2610-0000{id}", 1, "PCB-A", "Controller board A", 1, "L1", 200,
        new DateTime(2026, 11, 2, 1, 0, 0, DateTimeKind.Utc), new DateTime(2026, 11, 2, 5, 0, 0, DateTimeKind.Utc),
        "Released", 5, true, "Pending", null, 1, 1, null, null, null, null, null, null, null, 0, 200,
        ["Start", "Cancel", "ChangePriority", "PrintLabels"], rowVersion, [], []);

    private class WorkOrders : FakeWorkOrderApi
    {
        public List<WorkOrderSummaryDto> List { get; } = [Summary(1, priority: 1), Summary(2), Summary(3, "Closed")];
        public List<(string? Status, int? LineId, string? Search, int Page)> Queries { get; } = [];
        public (int Id, ChangePriorityRequest Request)? PriorityChange { get; private set; }
        public PreviewWorkOrderRequest? Previewed { get; private set; }
        public CreateWorkOrderRequest? Created { get; private set; }
        public Exception? Fail { get; set; }

        public override Task<PagedResult<WorkOrderSummaryDto>> GetWorkOrdersAsync(string? status, int? lineId, string? search, int page, int pageSize, CancellationToken ct = default)
        {
            Queries.Add((status, lineId, search, page));
            return Task.FromResult(new PagedResult<WorkOrderSummaryDto>(List, page, pageSize, List.Count));
        }

        public override Task<WorkOrderDetailDto> GetWorkOrderAsync(int id, CancellationToken ct = default) => Task.FromResult(Detail(id));

        public override Task<WorkOrderDetailDto> ChangePriorityAsync(int id, ChangePriorityRequest request, CancellationToken ct = default)
        {
            PriorityChange = (id, request);
            return Fail is null ? Task.FromResult(Detail(id)) : Task.FromException<WorkOrderDetailDto>(Fail);
        }

        public override Task<WorkOrderPreviewDto> PreviewAsync(PreviewWorkOrderRequest request, CancellationToken ct = default)
        {
            Previewed = request;
            if (Fail is not null) return Task.FromException<WorkOrderPreviewDto>(Fail);
            return Task.FromResult(new WorkOrderPreviewDto(request.ItemId, "PCB-A", request.PlannedQty, 1, 1, DateTime.UtcNow,
                [new WorkOrderMaterialDto(4, "RES-10K-0402", "Resistor", "pcs", 10, 12.3624m, 2472.48m, 2473m, 0, 0)],
                [new WorkOrderOperationDto(10, "SMT", 1, "WC-SMT", 30m, false, true, "/sop/PCB-A/SMT-v1.pdf", 0, 0)]));
        }

        public override Task<WorkOrderDetailDto> CreateAsync(CreateWorkOrderRequest request, CancellationToken ct = default)
        {
            Created = request;
            return Fail is null ? Task.FromResult(Detail(9)) : Task.FromException<WorkOrderDetailDto>(Fail);
        }
    }

    private sealed class Masters : FakeMasterDataApi
    {
        public override Task<IReadOnlyList<LineDto>> GetLinesAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<LineDto>>([new(2, 1, "L2", "Line 2", true), new(1, 1, "L1", "Line 1", true), new(3, 1, "L3", "Old", false)]);

        public override Task<PagedResult<ItemDto>> GetItemsAsync(string? search, string? type, bool? isActive, int page, int pageSize, CancellationToken ct = default)
        {
            ItemDto[] items = type == "Finished"
                ? [new(1, "PCB-A", "Controller board A", "Finished", "pcs", true, false, false, true)]
                : [new(2, "PCBA-SMT", "PCB-A after SMT", "Semi", "pcs", false, false, false, true)];
            return Task.FromResult(new PagedResult<ItemDto>(items, 1, pageSize, items.Length));
        }
    }

    private sealed class FakePages : IPageNavigator
    {
        public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
        public object? CurrentPage => null;
        public List<Type> Shown { get; } = [];

        public Task ShowAsync<TPage>(Func<TPage, Task>? init = null) where TPage : class
        {
            Shown.Add(typeof(TPage));
            return Task.CompletedTask;
        }

        public void Clear() { }
    }

    // ---------- list ----------

    private static async Task<(WorkOrderListViewModel Vm, WorkOrders Api)> ListAsync()
    {
        var api = new WorkOrders();
        var vm = new WorkOrderListViewModel(api, new Masters(), new FakePages(), new PlantClock());
        await vm.LoadAsync();
        return (vm, api);
    }

    [Fact]
    public async Task List_shows_planned_start_in_plant_time_and_progress()
    {
        var (vm, _) = await ListAsync();

        var row = vm.Rows[0];
        Assert.Equal("2026-11-02 08:00", row.PlannedStartText);   // 01:00 UTC = 08:00 in Vietnam
        Assert.Equal("0 / 200", row.Progress);
        Assert.Equal([Strings.Common_All, "L1", "L2"], vm.LineOptions.Select(l => l.Code));   // active lines only, sorted
    }

    [Fact]
    public async Task Filters_go_to_the_server_and_all_means_no_filter()
    {
        var (vm, api) = await ListAsync();
        Assert.Equal((null, null, null, 1), api.Queries[^1]);

        vm.StatusFilter = "Released";
        vm.LineFilter = vm.LineOptions.Single(l => l.Code == "L2");
        vm.SearchText = "  PCB-A ";
        await vm.SearchCommand.ExecuteAsync(null);

        Assert.Equal(("Released", (int?)2, "PCB-A", 1), api.Queries[^1]);
    }

    [Fact]
    public async Task Set_priority_sends_the_loaded_row_version_and_reloads()
    {
        var (vm, api) = await ListAsync();
        vm.Selected = vm.Rows.Single(r => r.Wo.Id == 2);
        vm.NewPriority = 1;
        var queries = api.Queries.Count;

        await vm.SetPriorityCommand.ExecuteAsync(null);

        Assert.Equal((2, new ChangePriorityRequest(1, "AAAAAAAAB9E=")), api.PriorityChange);
        Assert.Equal(queries + 1, api.Queries.Count);
    }

    [Fact]
    public async Task Priority_of_a_finished_order_cannot_be_set()
    {
        var (vm, _) = await ListAsync();

        vm.Selected = vm.Rows.Single(r => r.Wo.Status == "Closed");

        Assert.False(vm.SetPriorityCommand.CanExecute(null));
    }

    // 409: someone changed the order; the list reloads and says so in English from Strings.resx.
    [Fact]
    public async Task Conflict_reloads_and_keeps_the_message()
    {
        var (vm, api) = await ListAsync();
        api.Fail = new ApiException(HttpStatusCode.Conflict, "changed");
        vm.Selected = vm.Rows[0];
        var queries = api.Queries.Count;

        await vm.SetPriorityCommand.ExecuteAsync(null);

        Assert.Equal(Strings.WorkOrder_Conflict, vm.ErrorMessage);
        Assert.Equal(queries + 1, api.Queries.Count);
    }

    // ---------- create ----------

    private static async Task<(WorkOrderCreateViewModel Vm, WorkOrders Api, FakePages Pages)> CreateFormAsync()
    {
        var (api, pages) = (new WorkOrders(), new FakePages());
        var vm = new WorkOrderCreateViewModel(api, new Masters(), pages, new PlantClock());
        await vm.InitAsync();
        return (vm, api, pages);
    }

    [Fact]
    public async Task Form_offers_made_items_and_active_lines_and_defaults_to_tomorrow()
    {
        var (vm, _, _) = await CreateFormAsync();

        Assert.Equal(["PCB-A", "PCBA-SMT"], vm.ItemOptions.Select(i => i.Code));
        Assert.Equal("L1", vm.Line?.Code);
        Assert.Equal(new DateTime(2026, 10, 18), vm.StartDate);
        Assert.Equal(("08:00", "12:00"), (vm.StartTimeText, vm.EndTimeText));
    }

    [Theory]
    [InlineData(false, "200", "ItemId")]
    [InlineData(true, "abc", "PlannedQty")]
    [InlineData(true, "0", "PlannedQty")]
    [InlineData(true, "1.5", "PlannedQty")]
    public async Task Preview_checks_item_and_whole_quantity_before_calling_the_server(bool pickItem, string qty, string field)
    {
        var (vm, api, _) = await CreateFormAsync();
        if (pickItem) vm.Item = vm.ItemOptions[0];
        vm.PlannedQtyText = qty;

        await vm.PreviewCommand.ExecuteAsync(null);

        Assert.NotEmpty(vm.FieldErrors[field]);
        Assert.Null(api.Previewed);
    }

    [Fact]
    public async Task Preview_fills_materials_and_operations_and_a_new_quantity_clears_them()
    {
        var (vm, api, _) = await CreateFormAsync();
        vm.Item = vm.ItemOptions[0];

        await vm.PreviewCommand.ExecuteAsync(null);

        Assert.Equal(new PreviewWorkOrderRequest(1, 200), api.Previewed);
        Assert.Equal(2473m, Assert.Single(vm.Materials).RequiredQtyRounded);
        Assert.Single(vm.Operations);
        Assert.Contains("BOM v1", vm.PreviewInfo);

        vm.PlannedQtyText = "100";   // stale numbers must not stay on screen
        Assert.Empty(vm.Materials);
        Assert.Empty(vm.PreviewInfo);
    }

    [Fact]
    public async Task Save_sends_utc_converted_from_plant_time_and_returns_to_the_list()
    {
        var (vm, api, pages) = await CreateFormAsync();
        vm.Item = vm.ItemOptions[0];
        vm.Priority = 2;
        vm.RequireFai = false;

        await vm.SaveCommand.ExecuteAsync(null);

        var expected = new CreateWorkOrderRequest(1, 1, 200, new DateTime(2026, 10, 18, 1, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 10, 18, 5, 0, 0, DateTimeKind.Utc), 2, false);
        Assert.Equal(expected, api.Created);
        Assert.Equal(DateTimeKind.Utc, api.Created!.PlannedStart.Kind);
        Assert.Equal(typeof(WorkOrderListViewModel), pages.Shown.Single());
    }

    [Theory]
    [InlineData("12:00", "08:00", "PlannedEnd")]   // end before start
    [InlineData("8h", "12:00", "PlannedStart")]    // not HH:mm
    public async Task Save_refuses_a_bad_window_without_calling_the_server(string start, string end, string field)
    {
        var (vm, api, _) = await CreateFormAsync();
        vm.Item = vm.ItemOptions[0];
        (vm.StartTimeText, vm.EndTimeText) = (start, end);

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.NotEmpty(vm.FieldErrors[field]);
        Assert.Null(api.Created);
    }

    [Fact]
    public async Task Server_400_lands_under_the_box_and_422_on_the_page()
    {
        var (vm, api, pages) = await CreateFormAsync();
        vm.Item = vm.ItemOptions[0];
        api.Fail = new ApiException(HttpStatusCode.BadRequest, "Validation failed", null,
            new Dictionary<string, string[]> { ["ItemId"] = ["Item 'PCB-A' is inactive."] });

        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Equal("Item 'PCB-A' is inactive.", vm.FieldErrors["ItemId"]);

        api.Fail = new ApiException(HttpStatusCode.UnprocessableEntity, "Item 'PCB-A' has no approved routing in effect.");
        await vm.PreviewCommand.ExecuteAsync(null);
        Assert.Equal("Item 'PCB-A' has no approved routing in effect.", vm.ErrorMessage);
        Assert.Empty(pages.Shown);
    }
}
