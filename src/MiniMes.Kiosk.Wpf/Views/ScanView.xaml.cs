using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using MiniMes.Client.Core.ViewModels.Kiosk;

namespace MiniMes.Kiosk.Wpf.Views;

// View-only logic (spec M3 2.7, 2.9): keep the keyboard focus in ScanBox, poll the API health, feed the SOP
// viewer. No business rule here; the ViewModel does the scanning.
public partial class ScanView : UserControl
{
    private readonly DispatcherTimer _probe = new() { Interval = TimeSpan.FromSeconds(3) };
    private Window? _window;
    private ScanViewModel? _vm;

    public ScanView()
    {
        InitializeComponent();
        _probe.Tick += (_, _) => _vm?.CheckConnectionCommand.Execute(null);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        DataContextChanged += (_, _) => Attach(DataContext as ScanViewModel);
        // Whatever took the focus (a click on the SOP viewer, a closed overlay, Alt-Tab): give it back to the
        // scan box once the current input is handled, or the next scan types into nothing.
        ScanBox.LostKeyboardFocus += (_, _) => Dispatcher.BeginInvoke(FocusScanBox, DispatcherPriority.Input);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _window = Window.GetWindow(this);
        if (_window is not null) _window.Activated += OnWindowActivated;
        _probe.Start();
        FocusScanBox();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _probe.Stop();
        if (_window is not null) _window.Activated -= OnWindowActivated;
        Attach(null);
    }

    private void OnWindowActivated(object? sender, EventArgs e) => FocusScanBox();

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
