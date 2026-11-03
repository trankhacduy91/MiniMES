using System.Windows.Controls;
using System.Windows.Threading;

namespace MiniMes.Kiosk.Wpf.Views;

// View-only logic, like the scan screen: keep the keyboard focus in ScanBox and poll the API health.
public partial class PackingView : UserControl
{
    public PackingView()
    {
        InitializeComponent();
        _ = new HealthProbe(this, FocusScanBox);
        // A tapped mode, reason or cancel button never keeps the focus: the next scan must land in the box.
        ScanBox.LostKeyboardFocus += (_, _) => Dispatcher.BeginInvoke(FocusScanBox, DispatcherPriority.Input);
    }

    private void FocusScanBox()
    {
        if (IsLoaded && !ScanBox.IsKeyboardFocused) ScanBox.Focus();
    }
}
