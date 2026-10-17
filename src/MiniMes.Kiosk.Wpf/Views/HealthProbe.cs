using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MiniMes.Client.Core.ViewModels.Kiosk;

namespace MiniMes.Kiosk.Wpf.Views;

// View plumbing shared by the station screens (spec M3 2.7, 2.9): ask the API health every 3 s while the screen
// is shown, and run `refocus` when the window is activated again (Alt-Tab, a closed dialog of another app).
internal sealed class HealthProbe
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(3) };
    private Window? _window;

    public HealthProbe(UserControl view, Action refocus)
    {
        _timer.Tick += (_, _) => (view.DataContext as StationScreenViewModel)?.CheckConnectionCommand.Execute(null);
        EventHandler onActivated = (_, _) => refocus();
        view.Loaded += (_, _) =>
        {
            _window = Window.GetWindow(view);
            if (_window is not null) _window.Activated += onActivated;
            _timer.Start();
            refocus();
        };
        view.Unloaded += (_, _) =>
        {
            _timer.Stop();
            if (_window is not null) _window.Activated -= onActivated;
        };
    }
}
