using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.DependencyInjection;

namespace MiniMes.Client.Core.Navigation;

// A page that fetches its data when it is shown (list screens).
public interface ILoadable
{
    Task LoadAsync();
}

// M4 step 8: a page that holds something open (a SignalR connection) and must let it go when the user leaves.
// Not IDisposable on purpose: the DI container keeps a reference to every IDisposable Transient it creates from the
// root provider until the app exits (to dispose it then), so each visit to the dashboard would leak one ViewModel.
public interface IClosable
{
    Task CloseAsync();   // never throws
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
        CloseCurrent();
        CurrentPage = page; // show it first, so its "loading" state is visible
        if (init is not null) await init(page);
        else if (page is ILoadable loadable) await loadable.LoadAsync();
    }

    public void Clear()
    {
        CloseCurrent();
        CurrentPage = null;
    }

    // Fire and forget: closing a connection must not hold up the next page; CloseAsync never throws.
    private void CloseCurrent()
    {
        if (CurrentPage is IClosable closable) _ = closable.CloseAsync();
    }
}
