using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MiniMes.Client.Core.Api;
using MiniMes.Client.Core.Http;
using MiniMes.Client.Core.Navigation;
using MiniMes.Client.Core.Resources;
using MiniMes.Contracts.MasterData;

namespace MiniMes.Client.Core.ViewModels.MasterData;

// [v2] Excel import (spec M1 step 8f). The client only picks and uploads the file; the server reads and
// checks it. Import is enabled only after the SAME file passed Validate with 0 errors.
public partial class ImportViewModel(IMasterDataApi api, IDialogService dialogs) : ObservableObject
{
    public const string ExcelFilter = "Excel workbook (*.xlsx)|*.xlsx";

    public ObservableCollection<ImportErrorDto> Errors { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FileName))]
    [NotifyCanExecuteChangedFor(nameof(ValidateCommand))]
    private string? _filePath;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ImportCommand))]
    private bool _validatedWithoutErrors;

    [ObservableProperty]
    private string _resultMessage = string.Empty;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    public string FileName => FilePath is null ? Strings.Import_NoFile : Path.GetFileName(FilePath);

    private byte[]? _content;

    [RelayCommand]
    private void PickFile()
    {
        if (dialogs.PickFileToOpen(ExcelFilter) is not { } path) return;
        FilePath = path;
        _content = null;
        Errors.Clear();
        ResultMessage = string.Empty;
        ValidatedWithoutErrors = false; // a new file must be validated again
    }

    [RelayCommand(CanExecute = nameof(HasFile))]
    private Task ValidateAsync() => RunAsync(async () =>
    {
        _content = await File.ReadAllBytesAsync(FilePath!); // the bytes that were checked are the bytes imported
        var result = await api.ImportMasterDataAsync(FileName, _content, dryRun: true);
        Show(result);
        ValidatedWithoutErrors = result.Errors.Count == 0;
        ResultMessage = ValidatedWithoutErrors
            ? string.Format(Strings.Import_ValidOk, result.ItemCount, result.BomCount, result.RoutingCount)
            : string.Format(Strings.Import_ValidErrors, result.Errors.Count);
    });

    private bool HasFile() => FilePath is not null;

    // The server checks everything again (data may have changed since Validate).
    [RelayCommand(CanExecute = nameof(ValidatedWithoutErrors))]
    private Task ImportAsync() => RunAsync(async () =>
    {
        var result = await api.ImportMasterDataAsync(FileName, _content!, dryRun: false);
        Show(result);
        ValidatedWithoutErrors = false;
        ResultMessage = result.Committed
            ? string.Format(Strings.Import_Done, result.ItemCount, result.BomCount, result.RoutingCount)
            : string.Format(Strings.Import_ValidErrors, result.Errors.Count);
    });

    [RelayCommand]
    private Task DownloadTemplateAsync() => RunAsync(async () =>
    {
        if (dialogs.PickFileToSave("minimes-master-data-template.xlsx", ExcelFilter) is not { } path) return;
        await File.WriteAllBytesAsync(path, await api.GetImportTemplateAsync());
        ResultMessage = string.Format(Strings.Import_TemplateSaved, path);
    });

    private void Show(ImportResultDto result)
    {
        Errors.Clear();
        foreach (var e in result.Errors) Errors.Add(e);
    }

    private async Task RunAsync(Func<Task> action)
    {
        ErrorMessage = string.Empty;
        try
        {
            await action();
        }
        catch (ApiException ex)
        {
            ErrorMessage = ex.Message;
        }
        catch (IOException ex)
        {
            ErrorMessage = string.Format(Strings.Import_FileError, ex.Message);
        }
    }
}
