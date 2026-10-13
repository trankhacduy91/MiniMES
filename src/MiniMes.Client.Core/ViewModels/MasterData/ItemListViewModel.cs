using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MiniMes.Client.Core.Api;
using MiniMes.Client.Core.Http;
using MiniMes.Client.Core.Navigation;
using MiniMes.Client.Core.Resources;
using MiniMes.Contracts.MasterData;

namespace MiniMes.Client.Core.ViewModels.MasterData;

// Item list (spec M1 step 8a). Filtering and paging run on the server (GET /api/items): a real plant has
// thousands of items, and ICollectionView is a WPF type that Client.Core cannot use.
public partial class ItemListViewModel(IMasterDataApi api, IPageNavigator pages, IDialogService dialogs)
    : ObservableObject, ILoadable
{
    public const int PageSize = 50;

    public ObservableCollection<ItemDto> Items { get; } = [];

    // First entry = no filter. Values are the API's type names.
    public IReadOnlyList<string> TypeOptions { get; } = [Strings.Common_All, "Raw", "Semi", "Finished"];

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private string _typeFilter = Strings.Common_All;

    [ObservableProperty]
    private bool _showInactive;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EditCommand), nameof(DeactivateCommand))]
    private ItemDto? _selected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageText))]
    [NotifyCanExecuteChangedFor(nameof(PreviousPageCommand), nameof(NextPageCommand))]
    private int _page = 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageText))]
    [NotifyCanExecuteChangedFor(nameof(NextPageCommand))]
    private int _totalCount;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    private int PageCount => Math.Max(1, (TotalCount + PageSize - 1) / PageSize);

    public string PageText => string.Format(Strings.Common_PageInfo, Page, PageCount, TotalCount);

    public Task LoadAsync() => ReloadAsync();

    // Enter in the search box or the Search button: back to page 1 with the new filter.
    [RelayCommand]
    private Task SearchAsync()
    {
        Page = 1;
        return ReloadAsync();
    }

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private Task PreviousPageAsync()
    {
        Page--;
        return ReloadAsync();
    }

    private bool CanGoBack() => Page > 1;

    [RelayCommand(CanExecute = nameof(CanGoForward))]
    private Task NextPageAsync()
    {
        Page++;
        return ReloadAsync();
    }

    private bool CanGoForward() => Page < PageCount;

    [RelayCommand]
    private Task NewAsync() => pages.ShowAsync<ItemEditViewModel>(vm => vm.InitNewAsync());

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task EditAsync() => pages.ShowAsync<ItemEditViewModel>(vm => vm.InitEditAsync(Selected!.Id));

    private bool HasSelection() => Selected is not null;

    // Soft delete only (spec rule 13): history keeps the item, new BOMs and orders cannot pick it.
    [RelayCommand(CanExecute = nameof(CanDeactivate))]
    private async Task DeactivateAsync()
    {
        var item = Selected!;
        if (!dialogs.Confirm(string.Format(Strings.Common_ConfirmDeactivate, item.Code))) return;
        await RunAsync(async () =>
        {
            await api.DeactivateItemAsync(item.Id);
            await ReloadAsync();
        });
    }

    private bool CanDeactivate() => Selected is { IsActive: true };

    private Task ReloadAsync() => RunAsync(async () =>
    {
        var type = TypeFilter == Strings.Common_All ? null : TypeFilter;
        var search = string.IsNullOrWhiteSpace(SearchText) ? null : SearchText.Trim();
        var result = await api.GetItemsAsync(search, type, ShowInactive ? null : true, Page, PageSize);
        Items.Clear();
        foreach (var item in result.Items) Items.Add(item);
        TotalCount = result.TotalCount;
    });

    // Every API error ends here as a message on the screen, never as an unhandled exception.
    private async Task RunAsync(Func<Task> action)
    {
        ErrorMessage = string.Empty;
        try
        {
            await action();
        }
        catch (ApiException ex)
        {
            ErrorMessage = ex.Message;
        }
    }
}
