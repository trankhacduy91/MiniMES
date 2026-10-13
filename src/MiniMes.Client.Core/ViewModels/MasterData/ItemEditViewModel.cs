using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Net;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MiniMes.Client.Core.Api;
using MiniMes.Client.Core.Http;
using MiniMes.Client.Core.Navigation;
using MiniMes.Client.Core.Resources;
using MiniMes.Contracts.MasterData;

namespace MiniMes.Client.Core.ViewModels.MasterData;

// New / edit item (spec M1 step 8b, section 2.8). Two layers of validation: simple format rules here
// for instant feedback, the server for everything (its 400 per field and 409 land next to the box).
public partial class ItemEditViewModel : ObservableValidator
{
    // Same format as the server's MasterDataRules (Contracts cannot share Domain constants).
    public const string CodePattern = "^[A-Z0-9._-]+$";

    private readonly IMasterDataApi _api;
    private readonly IPageNavigator _pages;
    private int _id;

    public ItemEditViewModel(IMasterDataApi api, IPageNavigator pages)
    {
        _api = api;
        _pages = pages;
        // Client rule results go to the same FieldErrors the server answers use.
        ErrorsChanged += (_, e) => FieldErrors.Set(e.PropertyName!,
            GetErrors(e.PropertyName).OfType<ValidationResult>().FirstOrDefault()?.ErrorMessage);
    }

    public IReadOnlyList<string> TypeOptions { get; } = ["Raw", "Semi", "Finished"];
    public IReadOnlyList<string> UomOptions { get; } = ["pcs", "kg", "g", "m"];

    public FieldErrors FieldErrors { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    private bool _isNew = true;

    public string Title => IsNew ? Strings.ItemEdit_TitleNew : string.Format(Strings.ItemEdit_TitleEdit, Code);

    [ObservableProperty]
    [NotifyDataErrorInfo]
    [Required(ErrorMessageResourceType = typeof(Strings), ErrorMessageResourceName = nameof(Strings.Validation_Required))]
    [MaxLength(50, ErrorMessageResourceType = typeof(Strings), ErrorMessageResourceName = nameof(Strings.Validation_MaxLength))]
    [RegularExpression(CodePattern, ErrorMessageResourceType = typeof(Strings), ErrorMessageResourceName = nameof(Strings.Validation_CodeFormat))]
    private string _code = string.Empty;

    [ObservableProperty]
    [NotifyDataErrorInfo]
    [Required(ErrorMessageResourceType = typeof(Strings), ErrorMessageResourceName = nameof(Strings.Validation_Required))]
    [MaxLength(200, ErrorMessageResourceType = typeof(Strings), ErrorMessageResourceName = nameof(Strings.Validation_MaxLength))]
    private string _name = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditSkipIqc))]
    private string _type = "Raw";

    [ObservableProperty]
    private string _uom = "pcs";

    [ObservableProperty]
    private bool _isSerialized;

    [ObservableProperty]
    private bool _isLotControlled = true;

    [ObservableProperty]
    private bool _skipIqc;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    // IQC is the incoming inspection of bought material: only Raw items can skip it (rule 18).
    public bool CanEditSkipIqc => Type == "Raw";

    public Task InitNewAsync() => Task.CompletedTask;

    public async Task InitEditAsync(int id)
    {
        try
        {
            var item = await _api.GetItemAsync(id);
            _id = item.Id;
            IsNew = false;
            Code = item.Code;
            Name = item.Name;
            Type = item.Type;
            Uom = item.Uom;
            IsSerialized = item.IsSerialized;
            IsLotControlled = item.IsLotControlled;
            SkipIqc = item.SkipIqc;
            OnPropertyChanged(nameof(Title));
        }
        catch (ApiException ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    // Typing a lower-case code is common; the server only accepts upper case.
    partial void OnCodeChanging(string value) => FieldErrors.Set(nameof(Code), null);
    partial void OnNameChanging(string value) => FieldErrors.Set(nameof(Name), null);

    // Spec M1 1.2.1: traceability follows the type, so pre-fill the boxes the server would demand.
    partial void OnTypeChanged(string value)
    {
        if (!IsNew) return; // Type and flags are fixed after creation
        (IsSerialized, IsLotControlled) = value switch
        {
            "Finished" => (true, false),
            "Raw" => (false, true),
            _ => (false, false),
        };
        if (value != "Raw") SkipIqc = false;
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (IsNew) Code = Code.Trim().ToUpperInvariant();
        ValidateAllProperties(); // also boxes never touched
        if (HasErrors) return;

        ErrorMessage = string.Empty;
        try
        {
            if (IsNew)
                await _api.CreateItemAsync(new CreateItemRequest(Code, Name.Trim(), Type, Uom, IsSerialized, IsLotControlled, SkipIqc));
            else
                await _api.UpdateItemAsync(_id, new UpdateItemRequest(Name.Trim(), SkipIqc));
            await _pages.ShowAsync<ItemListViewModel>();
        }
        // Duplicate code is not a system error: show it next to the Code box (spec 2.8).
        catch (ApiException ex) when (ex.Status == HttpStatusCode.Conflict && IsNew)
        {
            FieldErrors.Set(nameof(Code), ex.Message);
        }
        catch (ApiException ex) when (ex.FieldErrors.Count > 0)
        {
            FieldErrors.SetAll(ex.FieldErrors);
        }
        catch (ApiException ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    [RelayCommand]
    private Task CancelAsync() => _pages.ShowAsync<ItemListViewModel>();
}
