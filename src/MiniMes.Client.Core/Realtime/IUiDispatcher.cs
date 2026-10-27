namespace MiniMes.Client.Core.Realtime;

// Spec M4 2.6: SignalR callbacks run on a background thread, and WPF lets only the UI thread touch controls and
// bound ObservableCollections. Every push goes through Post. Kept behind an interface so Client.Core never sees WPF
// and ViewModel tests run the action at once.
public interface IUiDispatcher
{
    // Queues the action on the UI thread and returns at once (never Dispatcher.Invoke: it can deadlock).
    void Post(Action action);
}
