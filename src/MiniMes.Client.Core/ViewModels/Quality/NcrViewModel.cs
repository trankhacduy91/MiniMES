using System.Collections.ObjectModel;
using System.Globalization;
using System.Net;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MiniMes.Client.Core.Api;
using MiniMes.Client.Core.Auth;
using MiniMes.Client.Core.Http;
using MiniMes.Client.Core.Navigation;
using MiniMes.Client.Core.Resources;
using MiniMes.Contracts.Quality;

namespace MiniMes.Client.Core.ViewModels.Quality;

// Spec M7 step 8: NCR list on the left, the selected NCR on the right with the buttons its status and the user's
// role allow (QC opens / reviews / proposes; a Supervisor approves or rejects the proposal; QC or Supervisor closes).
// The buttons are a convenience: the API checks role (403), status and "not your own proposal" (422) again.
// A "new NCR" form: by hand, or filled from an SPC alert ("Create NCR" on the chart).
public partial class NcrViewModel : ObservableObject, ILoadable
{
    public const string AllStatuses = "All";

    private readonly INcrApi _api;
    private readonly IMasterDataApi _master;
    private readonly IDialogService _dialogs;
    private readonly string _me;
    private readonly bool _isReviewer, _isApprover, _isQuality;

    public NcrViewModel(INcrApi api, IMasterDataApi master, ITokenStore tokens, IDialogService dialogs)
    {
        _api = api;
        _master = master;
        _dialogs = dialogs;
        _me = tokens.Session?.EmployeeCode ?? string.Empty;
        var roles = tokens.Session?.Roles ?? [];
        _isReviewer = roles.Any(r => r is "QC" or "Admin");
        _isApprover = roles.Any(r => r is "Supervisor" or "Admin");
        _isQuality = _isReviewer || _isApprover;
    }

    public IReadOnlyList<string> StatusFilters { get; } = [AllStatuses, "Open", "UnderReview", "Dispositioned", "Closed", "Cancelled"];
    public IReadOnlyList<string> Sources { get; } = ["Manual", "Inspection", "ScanNg", "Traceability", "Customer", "SpcAlert"];
    public IReadOnlyList<string> Dispositions { get; } = ["UseAsIs", "Rework", "Scrap", "ReturnToVendor"];
    public ObservableCollection<NcrListItemDto> Ncrs { get; } = [];
    public ObservableCollection<string> DefectCodes { get; } = [];

    [ObservableProperty] private string _statusFilter = AllStatuses;
    [ObservableProperty] private NcrListItemDto? _selectedRow;
    [ObservableProperty] private string _errorMessage = string.Empty;
    [ObservableProperty] private string _infoMessage = string.Empty;

    // ---- the selected NCR ----
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDetail), nameof(CanStartReview), nameof(CanPropose), nameof(CanApprove), nameof(CanReject),
        nameof(CanClose), nameof(CanCancel), nameof(ApproveHint), nameof(StatusText))]
    [NotifyCanExecuteChangedFor(nameof(StartReviewCommand), nameof(ProposeCommand), nameof(ApproveCommand), nameof(RejectProposalCommand),
        nameof(CloseNcrCommand), nameof(CancelNcrCommand))]
    private NcrDto? _detail;

    [ObservableProperty] private string _proposedDisposition = "Rework";
    [ObservableProperty] private string _dispositionNote = string.Empty;
    [ObservableProperty] private string _concessionRef = string.Empty;
    [ObservableProperty] private string _capaReference = string.Empty;

    public bool HasDetail => Detail is not null;
    public string StatusText => Detail is { } d ? $"{d.NcrNo} · {d.Status}" : string.Empty;
    public bool CanStartReview => _isReviewer && Detail?.Status == "Open";
    public bool CanPropose => _isReviewer && Detail?.Status == "UnderReview";
    // Q11: shown off for the proposer too; the API says it again with 422.
    public bool CanApprove => _isApprover && Detail is { Status: "UnderReview", Disposition: not null } d
                              && !string.Equals(d.ProposedBy, _me, StringComparison.OrdinalIgnoreCase);
    public bool CanReject => _isApprover && Detail is { Status: "UnderReview", Disposition: not null };
    public bool CanClose => _isQuality && Detail is { Status: "Dispositioned", ActiveHolds: 0 };
    public bool CanCancel => _isReviewer && Detail?.Status is "Open" or "UnderReview";
    public string ApproveHint => Detail is { Status: "UnderReview", Disposition: not null } d
                                 && string.Equals(d.ProposedBy, _me, StringComparison.OrdinalIgnoreCase)
        ? Strings.Ncr_CannotApproveOwn
        : Detail is { Status: "Dispositioned", ActiveHolds: > 0 } h
            ? string.Format(CultureInfo.CurrentCulture, Strings.Ncr_HoldsActive, h.ActiveHolds)
            : string.Empty;

    // ---- the new NCR form ----
    [ObservableProperty] private bool _isCreating;
    [ObservableProperty] private string _newSource = "Manual";
    [ObservableProperty] private string _newSourceRef = string.Empty;
    [ObservableProperty] private string? _newDefectCode;
    [ObservableProperty] private string _newDescription = string.Empty;
    [ObservableProperty] private int _newQty = 1;
    [ObservableProperty] private string _newSerials = string.Empty;   // one per line
    [ObservableProperty] private string _newLots = string.Empty;
    [ObservableProperty] private long? _newSpcAlertId;

    public async Task LoadAsync()
    {
        try
        {
            foreach (var d in (await _master.GetDefectCodesAsync()).Where(d => d.IsActive).OrderBy(d => d.Code)) DefectCodes.Add(d.Code);
        }
        catch (ApiException ex)
        {
            ErrorMessage = ex.Message;
            return;
        }
        await RefreshAsync();
    }

    // "Create NCR" on an SPC alert (step 7 chart): the form filled, the user adds or checks the targets.
    public async Task StartFromAlertAsync(SpcAlertDto alert, string description, string? serialNo)
    {
        await LoadAsync();
        NewForm();
        (NewSource, NewSourceRef, NewSpcAlertId, NewDescription) = ("SpcAlert", alert.Id.ToString(CultureInfo.InvariantCulture), alert.Id, description);
        NewSerials = serialNo ?? string.Empty;
    }

    partial void OnStatusFilterChanged(string value) => _ = RefreshAsync();

    partial void OnSelectedRowChanged(NcrListItemDto? value)
    {
        if (value is not null) _ = ShowAsync(value.Id);
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        try
        {
            var rows = await _api.GetListAsync(StatusFilter == AllStatuses ? null : StatusFilter);
            Ncrs.Clear();
            foreach (var r in rows) Ncrs.Add(r);
            ErrorMessage = string.Empty;
        }
        catch (ApiException ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    private async Task ShowAsync(int id)
    {
        try
        {
            Show(await _api.GetAsync(id));
        }
        catch (ApiException ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    private void Show(NcrDto ncr)
    {
        IsCreating = false;
        Detail = ncr;
        ProposedDisposition = ncr.Disposition ?? ProposedDisposition;
        DispositionNote = ncr.DispositionNote ?? string.Empty;
        ConcessionRef = ncr.ConcessionRef ?? string.Empty;
        CapaReference = ncr.CapaReference ?? string.Empty;
    }

    [RelayCommand]
    private void NewForm()
    {
        (Detail, SelectedRow, IsCreating) = (null, null, true);
        (NewSource, NewSourceRef, NewDefectCode, NewDescription, NewQty, NewSerials, NewLots, NewSpcAlertId) =
            ("Manual", string.Empty, null, string.Empty, 1, string.Empty, string.Empty, null);
        (ErrorMessage, InfoMessage) = (string.Empty, string.Empty);
    }

    [RelayCommand]
    private Task CreateAsync() => RunAsync(async () =>
    {
        var targets = Lines(NewSerials).Select(c => new NcrTargetRequest("Serial", c))
            .Concat(Lines(NewLots).Select(c => new NcrTargetRequest("Lot", c))).ToList();
        var ncr = await _api.CreateAsync(new CreateNcrRequest(NewSource, Blank(NewSourceRef), NewDefectCode, NewDescription, NewQty,
            targets, NewSpcAlertId));
        InfoMessage = string.Format(CultureInfo.CurrentCulture, Strings.Ncr_Created, ncr.NcrNo);
        return ncr;
    });

    [RelayCommand(CanExecute = nameof(CanStartReview))]
    private Task StartReviewAsync() => RunAsync(() => _api.StartReviewAsync(Detail!.Id, Detail.RowVersion));

    [RelayCommand(CanExecute = nameof(CanPropose))]
    private Task ProposeAsync() => RunAsync(() => _api.ProposeAsync(Detail!.Id,
        new ProposeDispositionRequest(ProposedDisposition, DispositionNote, Blank(ConcessionRef), Detail.RowVersion)));

    [RelayCommand(CanExecute = nameof(CanApprove))]
    private Task ApproveAsync()
    {
        if (!_dialogs.Confirm(string.Format(CultureInfo.CurrentCulture, Strings.Ncr_ConfirmApprove, Detail!.NcrNo, Detail.Disposition)))
            return Task.CompletedTask;
        return RunAsync(() => _api.ApproveAsync(Detail.Id, Detail.RowVersion));
    }

    [RelayCommand(CanExecute = nameof(CanReject))]
    private Task RejectProposalAsync() => RunAsync(() => _api.RejectProposalAsync(Detail!.Id, Detail.RowVersion));

    [RelayCommand(CanExecute = nameof(CanClose))]
    private Task CloseNcrAsync() => RunAsync(() => _api.CloseAsync(Detail!.Id, new CloseNcrRequest(Blank(CapaReference), Detail.RowVersion)));

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private Task CancelNcrAsync()
    {
        var reason = _dialogs.AskText(Strings.Ncr_Cancel, Strings.Ncr_CancelReason);
        if (string.IsNullOrWhiteSpace(reason)) return Task.CompletedTask;
        return RunAsync(() => _api.CancelAsync(Detail!.Id, new CancelNcrRequest(reason, Detail.RowVersion)));
    }

    // Every action answers the whole NCR: show it, refresh the list. 409 = someone else changed it: reload it so
    // the screen shows what is true now, and say so.
    private async Task RunAsync(Func<Task<NcrDto>> action)
    {
        ErrorMessage = string.Empty;
        try
        {
            var ncr = await action();
            Show(ncr);
            await RefreshAsync();
        }
        catch (ApiException ex)
        {
            ErrorMessage = ex.Message;
            if (ex.Status == HttpStatusCode.Conflict && Detail is { } d) await ShowAsync(d.Id);
        }
    }

    private static IEnumerable<string> Lines(string text)
        => text.Split(['\r', '\n', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase);

    private static string? Blank(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
