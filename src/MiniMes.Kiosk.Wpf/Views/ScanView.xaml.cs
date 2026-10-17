using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using MiniMes.Client.Core.ViewModels.Kiosk;

namespace MiniMes.Kiosk.Wpf.Views;

// View-only logic (spec M3 2.7, 2.9): keep the keyboard focus in ScanBox, poll the API health, feed the SOP
// viewer. No business rule here; the ViewModel does the scanning.
public partial class ScanView : UserControl
{
    private ScanViewModel? _vm;

    public ScanView()
    {
        InitializeComponent();
        _ = new HealthProbe(this, FocusScanBox);
        Unloaded += (_, _) => Attach(null);
        DataContextChanged += (_, _) => Attach(DataContext as ScanViewModel);
        // Whatever took the focus (a click on the SOP viewer, the NG panel, a closed overlay): give it back to the
        // scan box once the current input is handled, or the next scan types into nothing.
        ScanBox.LostKeyboardFocus += (_, _) => Dispatcher.BeginInvoke(FocusScanBox, DispatcherPriority.Input);
    }

    private void FocusScanBox()
    {
        if (IsLoaded && !ScanBox.IsKeyboardFocused) ScanBox.Focus();
    }

    private void Attach(ScanViewModel? vm)
    {
        if (_vm is not null) _vm.PropertyChanged -= OnViewModelChanged;
        _vm = vm;
        if (_vm is not null) _vm.PropertyChanged += OnViewModelChanged;
    }

    // WebView2 is set from code: its Source cannot be null, and it starts asynchronously.
    private async void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ScanViewModel.IsSopOpen) || _vm is null) return;
        if (_vm.IsSopOpen && _vm.SopFile is { } file)
        {
            try
            {
                await SopViewer.EnsureCoreWebView2Async();
                SopViewer.CoreWebView2.Navigate(new Uri(file).AbsoluteUri);
            }
            catch (Exception ex) when (ex is WebView2RuntimeNotFoundException or InvalidOperationException)
            {
                _vm.CloseSopCommand.Execute(null);   // no WebView2 runtime on this PC: keep scanning, no crash
            }
        }
        FocusScanBox();
    }
}
