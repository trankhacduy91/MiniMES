using System.ComponentModel;
using System.Net;
using MiniMes.Client.Core.Http;
using MiniMes.Client.Core.Navigation;
using MiniMes.Client.Core.Resources;
using MiniMes.Client.Core.ViewModels.MasterData;
using MiniMes.Contracts.Common;
using MiniMes.Contracts.MasterData;

namespace MiniMes.UnitTests.Client;

// Item screens without a window: list paging/filter/deactivate and the edit form's two validation layers.
public class ItemViewModelTests
{
    private static ItemDto Item(int id, string code, bool active = true)
        => new(id, code, code + " name", "Raw", "pcs", false, true, false, active);

    private sealed class ItemsApi : FakeMasterDataApi
    {
        public List<ItemDto> Items { get; } = [Item(1, "RES-10K"), Item(2, "CAP-100N")];
        public int Total { get; set; } = 2;
        public (string? Search, string? Type, bool? IsActive, int Page)? LastQuery { get; private set; }
        public List<int> Deactivated { get; } = [];
        public CreateItemRequest? Created { get; private set; }
        public (int Id, UpdateItemRequest Request)? Updated { get; private set; }
        public Exception? Fail { get; set; }

        public override Task<PagedResult<ItemDto>> GetItemsAsync(string? search, string? type, bool? isActive, int page, int pageSize, CancellationToken ct = default)
        {
            LastQuery = (search, type, isActive, page);
            return Fail is null ? Task.FromResult(new PagedResult<ItemDto>(Items, page, pageSize, Total)) : Task.FromException<PagedResult<ItemDto>>(Fail);
        }

        public override Task<ItemDto> GetItemAsync(int id, CancellationToken ct = default) => Task.FromResult(Items.Single(i => i.Id == id));

        public override Task DeactivateItemAsync(int id, CancellationToken ct = default)
        {
            Deactivated.Add(id);
            return Task.CompletedTask;
        }

        public override Task<ItemDto> CreateItemAsync(CreateItemRequest request, CancellationToken ct = default)
        {
            Created = request;
            return Fail is null ? Task.FromResult(Item(9, request.Code)) : Task.FromException<ItemDto>(Fail);
        }

        public override Task<ItemDto> UpdateItemAsync(int id, UpdateItemRequest request, CancellationToken ct = default)
        {
            Updated = (id, request);
            return Task.FromResult(Item(id, "X"));
        }
    }

    private sealed class FakePages : IPageNavigator
    {
        public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
        public object? CurrentPage { get; private set; }
        public List<Type> Shown { get; } = [];

        public Task ShowAsync<TPage>(Func<TPage, Task>? init = null) where TPage : class
        {
            Shown.Add(typeof(TPage));
            return Task.CompletedTask;
        }

        public void Clear() => CurrentPage = null;
    }

    private sealed class FakeDialogs(bool answer) : IDialogService
    {
        public int Asked { get; private set; }
        public bool Confirm(string message) { Asked++; return answer; }
        public string? PickFileToOpen(string filter) => null;
        public string? PickFileToSave(string defaultFileName, string filter) => null;
        public string? AskText(string title, string prompt) => null;
    }

    // ---------- list ----------

    [Fact]
    public async Task List_loads_active_items_of_every_type_by_default()
    {
        var api = new ItemsApi();
        var vm = new ItemListViewModel(api, new FakePages(), new FakeDialogs(true));

        await vm.LoadAsync();

        Assert.Equal(["RES-10K", "CAP-100N"], vm.Items.Select(i => i.Code));
        Assert.Equal((null, null, true, 1), (api.LastQuery!.Value.Search, api.LastQuery.Value.Type, api.LastQuery.Value.IsActive, api.LastQuery.Value.Page));
        Assert.Equal(string.Format(Strings.Common_PageInfo, 1, 1, 2), vm.PageText);
    }

    [Fact]
    public async Task Search_sends_the_filters_and_restarts_at_page_one()
    {
        var api = new ItemsApi { Total = 120 };
        var vm = new ItemListViewModel(api, new FakePages(), new FakeDialogs(true));
        await vm.LoadAsync();
        await vm.NextPageCommand.ExecuteAsync(null);

        vm.SearchText = "res";
        vm.TypeFilter = "Raw";
        vm.ShowInactive = true;
        await vm.SearchCommand.ExecuteAsync(null);

        Assert.Equal(("res", "Raw", (bool?)null, 1), (api.LastQuery!.Value.Search, api.LastQuery.Value.Type, api.LastQuery.Value.IsActive, api.LastQuery.Value.Page));
    }

    [Fact]
    public async Task Paging_buttons_follow_the_total()
    {
        var vm = new ItemListViewModel(new ItemsApi { Total = 120 }, new FakePages(), new FakeDialogs(true));
        await vm.LoadAsync();

        Assert.False(vm.PreviousPageCommand.CanExecute(null));
        Assert.True(vm.NextPageCommand.CanExecute(null));      // 120 rows = 3 pages of 50
        await vm.NextPageCommand.ExecuteAsync(null);
        await vm.NextPageCommand.ExecuteAsync(null);
        Assert.False(vm.NextPageCommand.CanExecute(null));
    }

    [Fact]
    public async Task Deactivate_asks_first_and_does_nothing_on_no()
    {
        var api = new ItemsApi();
        var dialogs = new FakeDialogs(answer: false);
        var vm = new ItemListViewModel(api, new FakePages(), dialogs);
        await vm.LoadAsync();
        vm.Selected = vm.Items[0];

        await vm.DeactivateCommand.ExecuteAsync(null);

        Assert.Equal(1, dialogs.Asked);
        Assert.Empty(api.Deactivated);
    }

    [Fact]
    public async Task Deactivate_on_yes_calls_the_api_and_is_disabled_for_inactive_items()
    {
        var api = new ItemsApi();
        var vm = new ItemListViewModel(api, new FakePages(), new FakeDialogs(true));
        await vm.LoadAsync();

        vm.Selected = vm.Items[0];
        await vm.DeactivateCommand.ExecuteAsync(null);
        vm.Selected = Item(3, "OLD", active: false);

        Assert.Equal([1], api.Deactivated);
        Assert.False(vm.DeactivateCommand.CanExecute(null));
    }

    [Fact]
    public async Task Api_error_becomes_a_message_on_the_screen()
    {
        var vm = new ItemListViewModel(new ItemsApi { Fail = new ApiException(null, Strings.Error_CannotReachServer) },
            new FakePages(), new FakeDialogs(true));

        await vm.LoadAsync();

        Assert.Equal(Strings.Error_CannotReachServer, vm.ErrorMessage);
    }

    [Fact]
    public async Task Edit_is_enabled_only_with_a_selection_and_opens_the_edit_page()
    {
        var pages = new FakePages();
        var vm = new ItemListViewModel(new ItemsApi(), pages, new FakeDialogs(true));
        await vm.LoadAsync();

        Assert.False(vm.EditCommand.CanExecute(null));
        vm.Selected = vm.Items[1];
        await vm.EditCommand.ExecuteAsync(null);

        Assert.Equal([typeof(ItemEditViewModel)], pages.Shown);
    }

    // ---------- edit ----------

    [Fact]
    public async Task Empty_or_badly_formatted_fields_are_stopped_on_the_client()
    {
        var api = new ItemsApi();
        var vm = new ItemEditViewModel(api, new FakePages());

        await vm.SaveCommand.ExecuteAsync(null);
        Assert.NotEmpty(vm.FieldErrors["Code"]);
        Assert.NotEmpty(vm.FieldErrors["Name"]);

        vm.Code = "RES 10K";
        Assert.Equal(Strings.Validation_CodeFormat, vm.FieldErrors["Code"]);
        Assert.Null(api.Created);                                // nothing reached the server
    }

    [Theory]
    [InlineData("Finished", true, false)]
    [InlineData("Semi", false, false)]
    [InlineData("Raw", false, true)]
    public void Choosing_a_type_presets_the_traceability_flags(string type, bool serialized, bool lot)
    {
        var vm = new ItemEditViewModel(new ItemsApi(), new FakePages()) { Type = "Semi" };

        vm.Type = type;

        Assert.Equal((serialized, lot), (vm.IsSerialized, vm.IsLotControlled));
        Assert.Equal(type == "Raw", vm.CanEditSkipIqc);
    }

    [Fact]
    public async Task Saving_a_new_item_upper_cases_the_code_and_returns_to_the_list()
    {
        var api = new ItemsApi();
        var pages = new FakePages();
        var vm = new ItemEditViewModel(api, pages) { Code = "res-1k", Name = " Resistor 1k " };

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal(("RES-1K", "Resistor 1k", "Raw", "pcs"), (api.Created!.Code, api.Created.Name, api.Created.Type, api.Created.Uom));
        Assert.Equal([typeof(ItemListViewModel)], pages.Shown);
    }

    // Spec step 8 check: a duplicate code shows next to the Code box, not as a system error.
    [Fact]
    public async Task Duplicate_code_409_is_shown_next_to_the_code_box()
    {
        var api = new ItemsApi { Fail = new ApiException(HttpStatusCode.Conflict, "Item code 'RES-1K' already exists.") };
        var pages = new FakePages();
        var vm = new ItemEditViewModel(api, pages) { Code = "RES-1K", Name = "Resistor" };

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal("Item code 'RES-1K' already exists.", vm.FieldErrors["Code"]);
        Assert.Empty(vm.ErrorMessage);
        Assert.Empty(pages.Shown);                               // stays on the form

        vm.Code = "RES-1K2";                                     // typing clears the server message
        Assert.Empty(vm.FieldErrors["Code"]);
    }

    [Fact]
    public async Task Server_400_messages_land_on_their_fields()
    {
        var api = new ItemsApi
        {
            Fail = new ApiException(HttpStatusCode.BadRequest, "x", fieldErrors: new Dictionary<string, string[]>
            {
                ["IsSerialized"] = ["A Raw item cannot be serialized."],
            }),
        };
        var vm = new ItemEditViewModel(api, new FakePages()) { Code = "RES-1K", Name = "Resistor" };

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal("A Raw item cannot be serialized.", vm.FieldErrors["IsSerialized"]);
    }

    [Fact]
    public async Task Edit_mode_loads_the_item_locks_the_key_fields_and_sends_only_name_and_skip_iqc()
    {
        var api = new ItemsApi();
        var vm = new ItemEditViewModel(api, new FakePages());

        await vm.InitEditAsync(2);
        vm.Name = "Capacitor 100nF";
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.False(vm.IsNew);
        Assert.Equal("CAP-100N", vm.Code);
        Assert.Equal((2, "Capacitor 100nF", false), (api.Updated!.Value.Id, api.Updated.Value.Request.Name, api.Updated.Value.Request.SkipIqc));
        Assert.Null(api.Created);
    }
}
