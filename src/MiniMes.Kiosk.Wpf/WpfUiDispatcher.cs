using System.Windows;
using MiniMes.Client.Core.Realtime;

namespace MiniMes.Kiosk.Wpf;

// IUiDispatcher for WPF (spec M4 2.6): SignalR pushes arrive on a background thread; InvokeAsync queues them on the
// UI thread without blocking the caller (Dispatcher.Invoke could deadlock with a UI thread waiting on the hub).
public sealed class WpfUiDispatcher : IUiDispatcher
{
    public void Post(Action action) => Application.Current?.Dispatcher.InvokeAsync(action);
}
