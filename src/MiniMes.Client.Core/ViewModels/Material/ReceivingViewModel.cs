using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MiniMes.Client.Core.Api;
using MiniMes.Client.Core.Http;
using MiniMes.Client.Core.Navigation;
using MiniMes.Client.Core.Resources;
using MiniMes.Contracts.Material;
using MiniMes.Contracts.MasterData;

namespace MiniMes.Client.Core.ViewModels.Material;

// Spec M6 step 2, the warehouse "Receiving" screen: one supplier delivery (item, supplier lot, N reels of Q, expiry)
// becomes N lots with their labels. The newest lots are listed below; one label can be previewed or printed again.
public partial class ReceivingViewModel(IMaterialApi api, IMasterDataApi masterData, IDialogService dialogs, TimeProvider time)
    : ObservableObject, ILoadable
{
    public const string ZplFilter = "ZPL label files (*.zpl)|*.zpl";
    public const int ListSize = 100;

    public ObservableCollection<ItemDto> ItemOptions { get; } = [];
    public ObservableCollection<LotRow> Lots { get; } = [];
    public FieldErrors FieldErrors { get; } = new();

    [ObservableProperty]
    private ItemDto? _item;

    [ObservableProperty]
    private string _supplierLot = string.Empty;

    [ObservableProperty]
    private string _qtyPerReelText = string.Empty;

    [ObservableProperty]
    private string _reelCountText = "1";

    // A date picker, not a text box: 03/09 vs 09/03 is the classic expiry mistake (spec 1.5).
    [ObservableProperty]
    private DateTime? _expiryDate;

    [ObservableProperty]
    private string _search = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PreviewLabelCommand), nameof(PrintLabelCommand))]
    private LotRow? _selectedLot;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLabelImage))]
    private byte[]? _labelImage;

    public bool HasLabelImage => LabelImage is not null;

    [ObservableProperty]
    private string _resultMessage = string.Empty;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    // Lot-controlled raw material only: the server refuses the rest anyway, the list should not offer it.
    public async Task LoadAsync()
    {
        await RunAsync(async () =>
        {
            ItemOptions.Clear();
            foreach (var i in (await masterData.GetItemsAsync(null, "Raw", true, 1, 200)).Items.Where(i => i.IsLotControlled))
                ItemOptions.Add(i);
            await LoadLotsAsync();
        });
    }

    [RelayCommand]
    private Task RefreshAsync() => RunAsync(LoadLotsAsync);

    [RelayCommand]
    private async Task ReceiveAsync()
    {
        FieldErrors.Clear();
        ResultMessage = string.Empty;
        if (!TryRead(out var request)) return;

        await RunAsync(async () =>
        {
            var result = await api.ReceiveAsync(request);
            var first = result.Lots[0];
            var pending = first.Status == "PendingIqc" ? Strings.Receiving_PendingIqc : string.Empty;
            ResultMessage = string.Format(Strings.Receiving_Received, result.Lots.Count, first.LotNo, result.Lots[^1].LotNo, pending);
            SupplierLot = string.Empty;   // the next delivery is another supplier lot; item and quantity often repeat
            await LoadLotsAsync();
            SelectedLot = Lots.FirstOrDefault(r => r.Lot.Id == first.Id);
            // Labels go to the printer right away: a reel without its label is a reel nobody can load.
            if (dialogs.PickFileToSave($"{first.LotNo}.zpl", ZplFilter) is { } path) await File.WriteAllTextAsync(path, result.Zpl);
        });
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task PreviewLabelAsync() => RunAsync(async () => LabelImage = await api.PreviewLabelAsync(SelectedLot!.Lot.Id));

    // A torn or lost label: the same label again (lot labels carry no print counter, unlike product labels).
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task PrintLabelAsync()
    {
        var lot = SelectedLot!.Lot;
        if (dialogs.PickFileToSave($"{lot.LotNo}.zpl", ZplFilter) is not { } path) return;
        await RunAsync(async () => await File.WriteAllTextAsync(path, await api.GetLabelAsync(lot.Id)));
    }

    private bool HasSelection() => SelectedLot is not null;

    partial void OnSelectedLotChanged(LotRow? value) => LabelImage = null;

    private async Task LoadLotsAsync()
    {
        var page = await api.GetLotsAsync(Search, null, 1, ListSize);
        Lots.Clear();
        foreach (var l in page.Items) Lots.Add(new LotRow(l, Local(l.ReceivedAt).ToString(DateTimeFormat, CultureInfo.InvariantCulture),
            l.ExpiryAt is { } e ? Local(e).ToString(DateFormat, CultureInfo.InvariantCulture) : string.Empty));
    }

    // Expiry as "03 SEP 2028" like the label: no day/month mix-up.
    public const string DateFormat = "dd MMM yyyy";
    public const string DateTimeFormat = "dd MMM yyyy HH:mm";

    private DateTime Local(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), time.LocalTimeZone);

    private bool TryRead(out ReceiveLotsRequest request)
    {
        request = null!;
        var ok = true;
        if (Item is null) { FieldErrors.Set("ItemId", Strings.Validation_Required); ok = false; }
        if (string.IsNullOrWhiteSpace(SupplierLot)) { FieldErrors.Set("SupplierLot", Strings.Validation_Required); ok = false; }
        if (!decimal.TryParse(QtyPerReelText.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var qty) || qty <= 0)
        {
            FieldErrors.Set("QtyPerReel", Strings.Receiving_QtyPositive);
            ok = false;
        }
        if (!int.TryParse(ReelCountText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var reels) || reels <= 0)
        {
            FieldErrors.Set("ReelCount", Strings.Receiving_ReelsWholeNumber);
            ok = false;
        }
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(time.GetUtcNow(), time.LocalTimeZone).DateTime);
        DateOnly? expiry = ExpiryDate is { } e ? DateOnly.FromDateTime(e) : null;
        if (expiry <= today) { FieldErrors.Set("ExpiryDate", Strings.Receiving_ExpiryFuture); ok = false; }
        if (ok) request = new ReceiveLotsRequest(Item!.Id, SupplierLot.Trim(), qty, reels, expiry);
        return ok;
    }

    // 400 lands under its box (ItemId, SupplierLot, QtyPerReel, ReelCount, ExpiryDate); anything else on the page.
    private async Task RunAsync(Func<Task> action)
    {
        ErrorMessage = string.Empty;
        try
        {
            await action();
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
}

// One row of the lot list: the lot and its times already in plant time.
public sealed record LotRow(MaterialLotDto Lot, string ReceivedText, string ExpiryText);
