using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MiniMes.Client.Core.Api;
using MiniMes.Client.Core.Http;
using MiniMes.Client.Core.Navigation;
using MiniMes.Client.Core.Resources;
using MiniMes.Contracts.MasterData;

namespace MiniMes.Client.Core.ViewModels.MasterData;

// BOM screen (spec M1 step 8c): pick an item, its versions, edit a Draft's lines, approve, see the
// effective tree and explode a quantity. Every rule (lock after approve, cycles, validity) stays on the
// server; the screen only enables what makes sense and shows the server's answer.
public partial class BomViewModel(IMasterDataApi api, IDialogService dialogs) : ObservableObject, ILoadable
{
    // Items that can have a BOM (Semi/Finished); components can be any active item.
    public ObservableCollection<ItemDto> Parents { get; } = [];
    public ObservableCollection<ItemDto> Components { get; } = [];
    public ObservableCollection<BomSummaryDto> Versions { get; } = [];
    public ObservableCollection<BomLineRow> Lines { get; } = [];
    public ObservableCollection<BomNodeViewModel> TreeRoots { get; } = [];
    public ObservableCollection<ExplosionLineDto> Explosion { get; } = [];

    [ObservableProperty]
    private ItemDto? _selectedParent;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDraft), nameof(StatusText))]
    [NotifyCanExecuteChangedFor(nameof(ApproveCommand), nameof(SaveLinesCommand), nameof(AddLineCommand), nameof(RemoveLineCommand))]
    private BomSummaryDto? _selectedVersion;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveLineCommand))]
    private BomLineRow? _selectedLine;

    [ObservableProperty]
    private string _explodeQty = "1";

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    [ObservableProperty]
    private string _infoMessage = string.Empty;

    // Approved = locked (spec rule 6): the grid is read-only and Approve is greyed out.
    public bool IsDraft => SelectedVersion?.Status == "Draft";

    public string StatusText => SelectedVersion is { } v
        ? string.Format(Strings.Bom_VersionStatus, v.Version, v.Status,
            v.EffectiveFrom?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "-",
            v.EffectiveTo?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "-")
        : string.Empty;

    public Task LoadAsync() => RunAsync(async () =>
    {
        // Item lists are small enough for one page each (master data, not transactions).
        var semi = await api.GetItemsAsync(null, "Semi", true, 1, 200);
        var finished = await api.GetItemsAsync(null, "Finished", true, 1, 200);
        var all = await api.GetItemsAsync(null, null, true, 1, 200);
        Parents.Clear();
        foreach (var i in finished.Items.Concat(semi.Items).OrderBy(i => i.Code)) Parents.Add(i);
        Components.Clear();
        foreach (var i in all.Items.OrderBy(i => i.Code)) Components.Add(i);
    });

    // Selection changes start loads that the UI does not await; tests (and the next load) await this.
    public Task Loading { get; private set; } = Task.CompletedTask;
    private Task _linesLoading = Task.CompletedTask;

    partial void OnSelectedParentChanged(ItemDto? value) => Loading = ReloadVersionsAsync(selectVersionId: null);

    partial void OnSelectedVersionChanged(BomSummaryDto? value) => Loading = _linesLoading = LoadLinesAsync();

    private Task ReloadVersionsAsync(int? selectVersionId) => RunAsync(async () =>
    {
        Versions.Clear();
        Lines.Clear();
        TreeRoots.Clear();
        Explosion.Clear();
        if (SelectedParent is not { } parent) return;

        foreach (var v in await api.GetBomsAsync(parent.Id)) Versions.Add(v);
        // DTOs are records: a reloaded version equal to the old one would not raise "changed" and its
        // lines would never reload. Clearing first forces a real change.
        SelectedVersion = null;
        SelectedVersion = Versions.FirstOrDefault(v => v.Id == selectVersionId) ?? Versions.FirstOrDefault();
        await _linesLoading; // the lines of the version just selected (never this task itself)
        await LoadTreeAsync();
    });

    private Task LoadLinesAsync() => RunAsync(async () =>
    {
        Lines.Clear();
        if (SelectedVersion is not { } v) return;
        var bom = await api.GetBomAsync(v.Id);
        foreach (var l in bom.Lines) Lines.Add(BomLineRow.From(l));
    });

    // The tree shows what is in force now (bom-tree endpoint), not the selected draft.
    private async Task LoadTreeAsync()
    {
        TreeRoots.Clear();
        if (SelectedParent is not { } parent || !Versions.Any(v => v.Status == "Approved")) return;
        try
        {
            TreeRoots.Add(new BomNodeViewModel(await api.GetBomTreeAsync(parent.Id, 1m, null), isRoot: true));
        }
        catch (ApiException ex)
        {
            InfoMessage = ex.Message; // e.g. a Semi without an approved BOM yet: not a screen error
        }
    }

    // New version = copy of the latest one (the usual small engineering change), as Draft.
    [RelayCommand]
    private Task NewVersionAsync() => RunAsync(async () =>
    {
        if (SelectedParent is not { } parent) return;
        var draft = await api.CreateBomDraftAsync(new CreateDraftRequest(parent.Id, CopyFromLatest: true));
        await ReloadVersionsAsync(draft.Id);
    });

    [RelayCommand(CanExecute = nameof(IsDraft))]
    private void AddLine() => Lines.Add(new BomLineRow { OperationSeq = Lines.LastOrDefault()?.OperationSeq ?? 10 });

    [RelayCommand(CanExecute = nameof(CanRemoveLine))]
    private void RemoveLine() => Lines.Remove(SelectedLine!);

    private bool CanRemoveLine() => IsDraft && SelectedLine is not null;

    // Saves the whole list (the API replaces every line). A cycle comes back as 400 with the path.
    [RelayCommand(CanExecute = nameof(IsDraft))]
    private Task SaveLinesAsync() => RunAsync(async () =>
    {
        var saved = await api.SaveBomLinesAsync(SelectedVersion!.Id, new SaveBomLinesRequest(Lines.Select(l => l.ToInput()).ToList()));
        InfoMessage = string.Format(Strings.Bom_Saved, saved.Lines.Count);
        await ReloadVersionsAsync(saved.Id);
    });

    [RelayCommand(CanExecute = nameof(CanApprove))]
    private async Task ApproveAsync()
    {
        var v = SelectedVersion!;
        if (!dialogs.Confirm(string.Format(Strings.Bom_ConfirmApprove, v.Version, v.ItemCode))) return;
        await RunAsync(async () =>
        {
            await api.ApproveBomAsync(v.Id, new ApproveVersionRequest());
            await ReloadVersionsAsync(v.Id);
        });
    }

    private bool CanApprove() => IsDraft && SelectedVersion!.LineCount > 0;

    [RelayCommand]
    private Task ExplodeAsync() => RunAsync(async () =>
    {
        Explosion.Clear();
        if (SelectedParent is not { } parent) return;
        if (!TryParseQty(ExplodeQty, out var qty))
        {
            ErrorMessage = Strings.Bom_QtyInvalid;
            return;
        }
        var result = await api.ExplodeAsync(parent.Id, qty, null);
        foreach (var line in result.Lines) Explosion.Add(line);
    });

    // Accept both "0.5" and "0,5": the user's keyboard may be Vietnamese.
    public static bool TryParseQty(string text, out decimal qty)
        => decimal.TryParse(text.Trim().Replace(',', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out qty) && qty > 0;

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
    }
}
