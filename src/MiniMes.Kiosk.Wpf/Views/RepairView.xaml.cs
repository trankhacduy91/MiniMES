using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Threading;
using MiniMes.Client.Core.ViewModels.Kiosk;

namespace MiniMes.Kiosk.Wpf.Views;

// View-only logic: scan mode keeps the focus in ScanBox (like the scan screen); when the form opens, the cursor
// goes to "Action taken"; when it closes, back to ScanBox for the next board.
public partial class RepairView : UserControl
{
    private RepairViewModel? _vm;

    public RepairView()
    {
        InitializeComponent();
        _ = new HealthProbe(this, FocusCurrent);
        Unloaded += (_, _) => Attach(null);
        DataContextChanged += (_, _) => Attach(DataContext as RepairViewModel);
        ScanBox.LostKeyboardFocus += (_, _) =>
        {
            if (_vm is { IsScanMode: true }) Dispatcher.BeginInvoke(FocusCurrent, DispatcherPriority.Input);
        };
    }

    private void FocusCurrent()
    {
        if (!IsLoaded) return;
        if (_vm is { IsFormOpen: true }) ActionBox.Focus();
        else if (!ScanBox.IsKeyboardFocused) ScanBox.Focus();
    }

    private void Attach(RepairViewModel? vm)
    {
        if (_vm is not null) _vm.PropertyChanged -= OnViewModelChanged;
        _vm = vm;
        if (_vm is not null) _vm.PropertyChanged += OnViewModelChanged;
    }

    // After the layout shows or hides the form, so the target box is visible when it gets the focus.
    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RepairViewModel.IsFormOpen))
            Dispatcher.BeginInvoke(FocusCurrent, DispatcherPriority.Loaded);
    }
}
