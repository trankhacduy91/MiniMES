using System.Windows.Controls;
using System.Windows.Threading;

namespace MiniMes.Kiosk.Wpf.Views;

// Same plumbing as ScanView: the scan box keeps the keyboard focus (the scanner types there), the API health is polled.
public partial class LoadMaterialView : UserControl
{
    public LoadMaterialView()
    {
        InitializeComponent();
        _ = new HealthProbe(this, FocusScanBox);
        ScanBox.LostKeyboardFocus += (_, _) => Dispatcher.BeginInvoke(FocusScanBox, DispatcherPriority.Input);
    }

    private void FocusScanBox()
    {
        if (IsLoaded && !ScanBox.IsKeyboardFocused) ScanBox.Focus();
    }
}
