using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.DependencyInjection;

namespace MiniMes.Client.Core.Navigation;

// A page that fetches its data when it is shown (list screens).
public interface ILoadable
{
    Task LoadAsync();
}

// The content area of the BackOffice shell: the menu and the pages themselves switch pages through
// it (list -> edit -> back). ViewModels come from DI as Transient, so every visit starts fresh.
public interface IPageNavigator : INotifyPropertyChanged
{
    object? CurrentPage { get; }

    // init: set up the new page (e.g. load the item to edit). Without it, ILoadable pages load themselves.
    Task ShowAsync<TPage>(Func<TPage, Task>? init = null) where TPage : class;

    void Clear();
}

public sealed partial class PageNavigator(IServiceProvider services) : ObservableObject, IPageNavigator
{
    [ObservableProperty]
    private object? _currentPage;

    public async Task ShowAsync<TPage>(Func<TPage, Task>? init = null) where TPage : class
    {
        var page = services.GetRequiredService<TPage>();
        CurrentPage = page; // show it first, so its "loading" state is visible
        if (init is not null) await init(page);
        else if (page is ILoadable loadable) await loadable.LoadAsync();
    }

    public void Clear() => CurrentPage = null;
}
