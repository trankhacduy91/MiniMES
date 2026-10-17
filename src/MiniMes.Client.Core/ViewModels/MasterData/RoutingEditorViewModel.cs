using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MiniMes.Client.Core.Api;
using MiniMes.Client.Core.Http;
using MiniMes.Client.Core.Navigation;
using MiniMes.Client.Core.Resources;
using MiniMes.Contracts.MasterData;

namespace MiniMes.Client.Core.ViewModels.MasterData;

// One editable routing step in the grid (Draft only).
public partial class RoutingStepRow : ObservableObject
{
    [ObservableProperty]
    private int _seq;

    [ObservableProperty]
    private string _operationCode = string.Empty;

    [ObservableProperty]
    private int _workCenterId;

    [ObservableProperty]
    private decimal _idealCycleTimeSec = 30m;

    [ObservableProperty]
    private bool _isInspection;

    [ObservableProperty]
    private bool _isMandatory = true;

    [ObservableProperty]
    private string? _sopUrl;

    public static RoutingStepRow From(RoutingStepDto s) => new()
    {
        Seq = s.Seq, OperationCode = s.OperationCode, WorkCenterId = s.WorkCenterId, IdealCycleTimeSec = s.IdealCycleTimeSec,
        IsInspection = s.IsInspection, IsMandatory = s.IsMandatory, SopUrl = s.SopUrl,
    };

    public RoutingStepInput ToInput()
        => new(Seq, OperationCode.Trim().ToUpperInvariant(), WorkCenterId, IdealCycleTimeSec, IsInspection, IsMandatory,
            string.IsNullOrWhiteSpace(SopUrl) ? null : SopUrl.Trim());
}

// Routing editor (spec M1 step 8d/8f, section 2.7): versions like the BOM screen, plus Up/Down to reorder
// the steps. Moving renumbers Seq 10, 20, 30... so the order on screen is the order route check (M3) uses.
public partial class RoutingEditorViewModel(IMasterDataApi api, IDialogService dialogs) : ObservableObject, ILoadable
{
    public const int SeqStep = 10;

    public ObservableCollection<ItemDto> Parents { get; } = [];
    public ObservableCollection<WorkCenterDto> WorkCenters { get; } = [];
    public ObservableCollection<RoutingSummaryDto> Versions { get; } = [];
    public ObservableCollection<RoutingStepRow> Steps { get; } = [];

    [ObservableProperty]
    private ItemDto? _selectedParent;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDraft), nameof(StatusText))]
    [NotifyCanExecuteChangedFor(nameof(ApproveCommand), nameof(SaveStepsCommand), nameof(AddStepCommand),
        nameof(RemoveStepCommand), nameof(MoveUpCommand), nameof(MoveDownCommand))]
    private RoutingSummaryDto? _selectedVersion;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveStepCommand), nameof(MoveUpCommand), nameof(MoveDownCommand))]
    private RoutingStepRow? _selectedStep;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    [ObservableProperty]
    private string _infoMessage = string.Empty;

    public bool IsDraft => SelectedVersion?.Status == "Draft";

    public string StatusText => SelectedVersion is { } v
        ? string.Format(Strings.Bom_VersionStatus, v.Version, v.Status,
            v.EffectiveFrom?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "-",
            v.EffectiveTo?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "-")
        : string.Empty;

    // Selection changes start loads that the UI does not await; tests await this.
    public Task Loading { get; private set; } = Task.CompletedTask;
    private Task _stepsLoading = Task.CompletedTask;

    public Task LoadAsync() => RunAsync(async () =>
    {
        var semi = await api.GetItemsAsync(null, "Semi", true, 1, 200);
        var finished = await api.GetItemsAsync(null, "Finished", true, 1, 200);
        Parents.Clear();
        foreach (var i in finished.Items.Concat(semi.Items).OrderBy(i => i.Code)) Parents.Add(i);
        WorkCenters.Clear();
        foreach (var w in await api.GetWorkCentersAsync(isActive: true)) WorkCenters.Add(w);
    });

    partial void OnSelectedParentChanged(ItemDto? value) => Loading = ReloadVersionsAsync(selectVersionId: null);

    partial void OnSelectedVersionChanged(RoutingSummaryDto? value) => Loading = _stepsLoading = LoadStepsAsync();

    private Task ReloadVersionsAsync(int? selectVersionId) => RunAsync(async () =>
    {
        Versions.Clear();
        Steps.Clear();
        if (SelectedParent is not { } parent) return;
        foreach (var v in await api.GetRoutingsAsync(parent.Id)) Versions.Add(v);
        // DTOs are records: a reloaded version equal to the old one would not raise "changed" and its
        // lines would never reload. Clearing first forces a real change.
        SelectedVersion = null;
        SelectedVersion = Versions.FirstOrDefault(v => v.Id == selectVersionId) ?? Versions.FirstOrDefault();
        await _stepsLoading;
    });

    private Task LoadStepsAsync() => RunAsync(async () =>
    {
        Steps.Clear();
        if (SelectedVersion is not { } v) return;
        var routing = await api.GetRoutingAsync(v.Id);
        foreach (var s in routing.Steps.OrderBy(s => s.Seq)) Steps.Add(RoutingStepRow.From(s));
    });

    [RelayCommand]
    private Task NewVersionAsync() => RunAsync(async () =>
    {
        if (SelectedParent is not { } parent) return;
        var draft = await api.CreateRoutingDraftAsync(new CreateDraftRequest(parent.Id, CopyFromLatest: true));
        await ReloadVersionsAsync(draft.Id);
    });

    [RelayCommand(CanExecute = nameof(IsDraft))]
    private void AddStep()
    {
        var step = new RoutingStepRow
        {
            Seq = (Steps.Count == 0 ? 0 : Steps.Max(s => s.Seq)) + SeqStep,
            WorkCenterId = WorkCenters.FirstOrDefault()?.Id ?? 0,
        };
        Steps.Add(step);
        SelectedStep = step;
    }

    [RelayCommand(CanExecute = nameof(CanRemoveStep))]
    private void RemoveStep() => Steps.Remove(SelectedStep!);

    private bool CanRemoveStep() => IsDraft && SelectedStep is not null;

    [RelayCommand(CanExecute = nameof(CanMoveUp))]
    private void MoveUp() => MoveSelected(-1);

    private bool CanMoveUp() => IsDraft && SelectedStep is not null && Steps.IndexOf(SelectedStep) > 0;

    [RelayCommand(CanExecute = nameof(CanMoveDown))]
    private void MoveDown() => MoveSelected(+1);

    private bool CanMoveDown() => IsDraft && SelectedStep is not null && Steps.IndexOf(SelectedStep) < Steps.Count - 1;

    private void MoveSelected(int delta)
    {
        var i = Steps.IndexOf(SelectedStep!);
        Steps.Move(i, i + delta);
        for (var k = 0; k < Steps.Count; k++) Steps[k].Seq = (k + 1) * SeqStep; // 10, 20, 30...
        // The selected row is the same object, only its position changed: re-check Up/Down by hand.
        MoveUpCommand.NotifyCanExecuteChanged();
        MoveDownCommand.NotifyCanExecuteChanged();
    }

    // Saves the full list (the API replaces every step). Duplicate Seq, bad codes, minutes typed as
    // seconds and unknown work centers come back as 400 and are shown as the page error.
    [RelayCommand(CanExecute = nameof(IsDraft))]
    private Task SaveStepsAsync() => RunAsync(async () =>
    {
        var saved = await api.SaveRoutingStepsAsync(SelectedVersion!.Id,
            new SaveRoutingStepsRequest(Steps.Select(s => s.ToInput()).ToList()));
        InfoMessage = string.Format(Strings.Routing_Saved, saved.Steps.Count);
        await ReloadVersionsAsync(saved.Id);
    });

    [RelayCommand(CanExecute = nameof(CanApprove))]
    private async Task ApproveAsync()
    {
        var v = SelectedVersion!;
        if (!dialogs.Confirm(string.Format(Strings.Routing_ConfirmApprove, v.Version, v.ItemCode))) return;
        await RunAsync(async () =>
        {
            await api.ApproveRoutingAsync(v.Id, new ApproveVersionRequest());
            await ReloadVersionsAsync(v.Id);
        });
    }

    private bool CanApprove() => IsDraft && SelectedVersion!.StepCount > 0;

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
