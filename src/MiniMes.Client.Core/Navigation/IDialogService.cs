namespace MiniMes.Client.Core.Navigation;

// Things only the UI framework can do (message boxes, file pickers). WPF implements it; tests fake it,
// so ViewModels never reference MessageBox or OpenFileDialog.
public interface IDialogService
{
    bool Confirm(string message);

    // Returns the chosen path, or null when the user cancels.
    string? PickFileToOpen(string filter);
    string? PickFileToSave(string defaultFileName, string filter);

    // A short free text (the reason of a hold, cancel or reprint). Null when the user cancels.
    string? AskText(string title, string prompt);
}
