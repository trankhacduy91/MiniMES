using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
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

    // A small modal window built in code: one prompt, one text box, OK / Cancel. Enter = OK, Esc = Cancel.
    public string? AskText(string title, string prompt)
    {
        var input = new TextBox { Margin = new Thickness(0, 6, 0, 12), Padding = new Thickness(3), MinWidth = 360 };
        AutomationProperties.SetAutomationId(input, "AskTextInput");
        var ok = new Button { Content = Strings.Common_Ok, IsDefault = true, MinWidth = 80, Margin = new Thickness(0, 0, 6, 0) };
        AutomationProperties.SetAutomationId(ok, "AskTextOk");
        var cancel = new Button { Content = Strings.Common_Cancel, IsCancel = true, MinWidth = 80 };
        AutomationProperties.SetAutomationId(cancel, "AskTextCancel");

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = prompt, TextWrapping = TextWrapping.Wrap, MaxWidth = 360 });
        panel.Children.Add(input);
        panel.Children.Add(buttons);

        var window = new Window
        {
            Title = title, Content = panel, SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = Application.Current.MainWindow,
        };
        ok.Click += (_, _) => window.DialogResult = true;
        window.Loaded += (_, _) => input.Focus();
        return window.ShowDialog() == true ? input.Text.Trim() : null;
    }
}
