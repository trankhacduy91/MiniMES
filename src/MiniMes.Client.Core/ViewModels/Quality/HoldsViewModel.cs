using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MiniMes.Client.Core.Api;
using MiniMes.Client.Core.Auth;
using MiniMes.Client.Core.Http;
using MiniMes.Client.Core.Navigation;
using MiniMes.Client.Core.Resources;
using MiniMes.Contracts.Quality;

namespace MiniMes.Client.Core.ViewModels.Quality;

// One Hold action in the list. HeldAt in plant time.
public sealed record HoldBatchRow(HoldBatchDto Dto, string HeldAt)
{
    public string Targets => string.Format(CultureInfo.CurrentCulture, Strings.Holds_Targets, Dto.ActiveSerials, Dto.ActiveLots);
}

// Spec M7 step 9: the Holds screen. Active holds (one row per Hold action), Release with a note (Q9: QC or Supervisor,
// who and why are recorded), and a new hold by serials, carton or pallet (the panel). Holds by lot start from the
// trace screen, holds by machine window from the SPC chart: there the target is known.
public partial class HoldsViewModel : ObservableObject, ILoadable
{
    private readonly IHoldApi _api;
    private readonly IDialogService _dialogs;
    private readonly TimeProvider _time;

    public HoldsViewModel(IHoldApi api, ITokenStore tokens, IDialogService dialogs, TimeProvider time, HoldPanelViewModel panel)
    {
        _api = api;
        _dialogs = dialogs;
        _time = time;
        Panel = panel;
        Panel.Held += OnHeld;
        CanAct = (tokens.Session?.Roles ?? []).Any(r => r is "QC" or "Supervisor" or "Admin");
    }

    public HoldPanelViewModel Panel { get; }
    public bool CanAct { get; }
    public ObservableCollection<HoldBatchRow> Batches { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ReleaseCommand))]
    private HoldBatchRow? _selected;

    [ObservableProperty] private string _errorMessage = string.Empty;
    [ObservableProperty] private string _infoMessage = string.Empty;

    public Task LoadAsync() => RefreshAsync();

    private void OnHeld(HoldResultDto result) => _ = RefreshAsync();

    [RelayCommand]
    public async Task RefreshAsync()
    {
        try
        {
            var rows = await _api.GetBatchesAsync();
            Batches.Clear();
            foreach (var b in rows) Batches.Add(new HoldBatchRow(b, $"{Local(b.HeldAt):dd/MM HH:mm}"));
            ErrorMessage = string.Empty;
        }
        catch (ApiException ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    [RelayCommand]
    private Task NewHoldAsync() => Panel.OpenAsync("Serials", string.Empty, canChooseScope: true);

    private bool CanRelease() => CanAct && Selected is not null;

    // A note is required (Q9). The targets another hold still keeps stay Hold (Q8): the answer says how many.
    [RelayCommand(CanExecute = nameof(CanRelease))]
    private async Task ReleaseAsync()
    {
        var batch = Selected!.Dto;
        var note = _dialogs.AskText(Strings.Holds_Release, string.Format(CultureInfo.CurrentCulture, Strings.Holds_ReleaseNote,
            batch.Scope, batch.ScopeRef, batch.Reason));
        if (string.IsNullOrWhiteSpace(note)) return;
        (ErrorMessage, InfoMessage) = (string.Empty, string.Empty);
        try
        {
            var r = await _api.ReleaseAsync(batch.BatchId, note.Trim());
            InfoMessage = string.Format(CultureInfo.CurrentCulture, Strings.Holds_Released, r.Released, r.Restored, r.StillHeld);
        }
        catch (ApiException ex)
        {
            ErrorMessage = ex.Message;
        }
        await RefreshAsync();
    }

    private DateTime Local(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), _time.LocalTimeZone);
}
