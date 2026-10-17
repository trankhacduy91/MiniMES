using System.Collections.ObjectModel;
using System.Net;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MiniMes.Client.Core.Http;
using MiniMes.Client.Core.Navigation;
using MiniMes.Client.Core.Resources;

namespace MiniMes.Client.Core.ViewModels.MasterData;

public enum CatalogFieldKind { Text, Bool, Choice }

// One box of the generic edit form. Key = the API field name, so a 400 for "MqttTopicKey" lands here.
public partial class CatalogField(string key, string label, CatalogFieldKind kind) : ObservableObject
{
    public string Key { get; } = key;
    public string Label { get; } = label;
    public CatalogFieldKind Kind { get; } = kind;
    public IReadOnlyList<string> Options { get; init; } = [];
    public string? Hint { get; init; }
    public bool IsReadOnly { get; init; }

    [ObservableProperty]
    private string _text = string.Empty;

    [ObservableProperty]
    private bool _flag;

    [ObservableProperty]
    private string _error = string.Empty;

    partial void OnTextChanged(string value) => Error = string.Empty;
}

// Format: .NET format string for dates/times ("yyyy-MM-dd"), so the grid does not depend on the PC's culture.
public sealed record CatalogColumn(string Header, string Path, bool IsCheckBox = false, double Width = 120, string? Format = null);

// Shared screen for the small masters (spec M1 step 8e: "one generic view if possible"): a grid on the
// left, an edit form on the right. Subclasses only say which columns and fields exist and how to call
// the API; listing, selection, confirm, error mapping and the form life cycle live here once.
public abstract partial class CatalogViewModel(IDialogService dialogs) : ObservableObject, ILoadable
{
    public abstract string Title { get; }
    public abstract IReadOnlyList<CatalogColumn> Columns { get; }
    public virtual string RemoveLabel => Strings.Common_Deactivate;
    public virtual string? Hint => null;

    public ObservableCollection<object> Rows { get; } = [];
    public ObservableCollection<CatalogField> Fields { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EditCommand), nameof(RemoveCommand))]
    private object? _selected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FormTitle))]
    private bool _isEditing;

    private bool _isNew;
    private object? _editing;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    public string FormTitle => _isNew ? Strings.Common_New : Strings.Common_Edit;

    public async Task LoadAsync() => await RunAsync(ReloadAsync);

    [RelayCommand]
    private Task RefreshAsync() => RunAsync(ReloadAsync);

    [RelayCommand]
    private Task NewAsync() => RunAsync(async () => OpenForm(isNew: true, null, await NewFieldsAsync()));

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task EditAsync() => RunAsync(async () => OpenForm(isNew: false, Selected, await EditFieldsAsync(Selected!)));

    private bool HasSelection() => Selected is not null;

    [RelayCommand(CanExecute = nameof(CanRemoveSelected))]
    private async Task RemoveAsync()
    {
        var row = Selected!;
        if (!dialogs.Confirm(string.Format(RemoveConfirmFormat, Describe(row)))) return;
        await RunAsync(async () =>
        {
            await RemoveRowAsync(row);
            await ReloadAsync();
        });
    }

    private bool CanRemoveSelected() => Selected is { } row && CanRemove(row);

    [RelayCommand]
    private async Task SaveAsync()
    {
        foreach (var f in Fields) f.Error = string.Empty;
        ErrorMessage = string.Empty;
        var byKey = Fields.ToDictionary(f => f.Key);
        try
        {
            if (!await SaveRowAsync(_isNew, _editing, byKey)) return; // client-side check failed, errors already set
            IsEditing = false;
            await ReloadAsync();
        }
        catch (ApiException ex) when (ex.FieldErrors.Count > 0)
        {
            foreach (var (key, messages) in ex.FieldErrors)
                if (byKey.TryGetValue(key, out var field)) field.Error = messages.FirstOrDefault() ?? string.Empty;
                else ErrorMessage = messages.FirstOrDefault() ?? ex.Message; // e.g. "Breaks" mapped to a field that does not exist
        }
        catch (ApiException ex) when (ex.Status == HttpStatusCode.Conflict && byKey.TryGetValue("Code", out var code) && _isNew)
        {
            code.Error = ex.Message;
        }
        catch (ApiException ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    [RelayCommand]
    private void Cancel() => IsEditing = false;

    // ---------- what each catalog provides ----------

    protected virtual string RemoveConfirmFormat => Strings.Common_ConfirmDeactivate;
    protected abstract Task<IEnumerable<object>> FetchAsync();
    protected abstract Task<IEnumerable<CatalogField>> NewFieldsAsync();
    protected abstract Task<IEnumerable<CatalogField>> EditFieldsAsync(object row);

    // Returns false when a client-side check failed (set field.Error on the bad boxes, call nothing).
    protected abstract Task<bool> SaveRowAsync(bool isNew, object? row, IReadOnlyDictionary<string, CatalogField> fields);
    protected abstract Task RemoveRowAsync(object row);
    protected abstract string Describe(object row);
    protected abstract bool CanRemove(object row);

    // ---------- helpers ----------

    private void OpenForm(bool isNew, object? row, IEnumerable<CatalogField> fields)
    {
        _isNew = isNew;
        _editing = row;
        Fields.Clear();
        foreach (var f in fields) Fields.Add(f);
        ErrorMessage = string.Empty;
        IsEditing = true;
        OnPropertyChanged(nameof(FormTitle));
    }

    private async Task ReloadAsync()
    {
        var rows = await FetchAsync();
        Rows.Clear();
        foreach (var r in rows) Rows.Add(r);
    }

    protected async Task RunAsync(Func<Task> action)
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
    }

    protected static CatalogField Text(string key, string label, string value = "", bool readOnly = false, string? hint = null)
        => new(key, label, CatalogFieldKind.Text) { Text = value, IsReadOnly = readOnly, Hint = hint };

    protected static CatalogField Bool(string key, string label, bool value = false)
        => new(key, label, CatalogFieldKind.Bool) { Flag = value };

    protected static CatalogField Choice(string key, string label, IReadOnlyList<string> options, string? value = null, bool readOnly = false)
        => new(key, label, CatalogFieldKind.Choice) { Options = options, Text = value ?? options.FirstOrDefault() ?? "", IsReadOnly = readOnly };
}
