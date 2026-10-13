using System.Windows;
using Microsoft.Win32;
using MiniMes.Client.Core.Navigation;
using MiniMes.Client.Core.Resources;

namespace MiniMes.BackOffice.Wpf;

// The WPF side of IDialogService: the only place that shows message boxes and file pickers.
public sealed class DialogService : IDialogService
{
    public bool Confirm(string message)
        => MessageBox.Show(Application.Current.MainWindow!, message, Strings.BackOffice_Title,
            MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    public string? PickFileToOpen(string filter)
    {
        var dialog = new OpenFileDialog { Filter = filter };
        return dialog.ShowDialog(Application.Current.MainWindow) == true ? dialog.FileName : null;
    }

    public string? PickFileToSave(string defaultFileName, string filter)
    {
        var dialog = new SaveFileDialog { FileName = defaultFileName, Filter = filter };
        return dialog.ShowDialog(Application.Current.MainWindow) == true ? dialog.FileName : null;
    }
}
