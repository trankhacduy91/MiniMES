using System.ComponentModel;
using System.Net;
using MiniMes.Client.Core.Api;
using MiniMes.Client.Core.Auth;
using MiniMes.Client.Core.Http;
using MiniMes.Client.Core.Navigation;
using MiniMes.Client.Core.ViewModels.Quality;
using MiniMes.Contracts.Auth;
using MiniMes.Contracts.MasterData;
using MiniMes.Contracts.Quality;

namespace MiniMes.UnitTests.Client;

// Records the page asked for and keeps its init, so a test can run it on a page it builds itself.
internal sealed class RecordingPages : IPageNavigator
{
    public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
    public object? CurrentPage => null;
    public List<Type> Shown { get; } = [];
    public Delegate? LastInit { get; private set; }

    public Task ShowAsync<TPage>(Func<TPage, Task>? init = null) where TPage : class
    {
        Shown.Add(typeof(TPage));
        LastInit = init;
        return Task.CompletedTask;
    }

    public void Clear() { }
}

internal sealed class FakeNcrApi : INcrApi
{
    private int _version;
    public Dictionary<int, NcrDto> Store { get; } = [];
    public List<string> Calls { get; } = [];
    public CreateNcrRequest? Created { get; private set; }
    public ApiException? Fail { get; set; }

    public static NcrDto Ncr(int id, string status, string? disposition = null, string? proposedBy = null, int holds = 0)
        => new(id, $"NCR-{id:D6}", status, "Manual", null, "MEAS-OOS", "3V3 rail at the limit", 3, disposition,
            disposition is null ? null : "note", null, null, "qc01", DateTime.UtcNow, null, proposedBy, null, null, null, null, null,
            null, [new("Serial", 5, "PCBA2611040000015", "Hold")], holds, null, "v0");

    public Task<IReadOnlyList<NcrListItemDto>> GetListAsync(string? status, CancellationToken ct = default)
    {
        Calls.Add($"list {status ?? "*"}");
        return Task.FromResult<IReadOnlyList<NcrListItemDto>>(Store.Values.Where(n => status is null || n.Status == status)
            .Select(n => new NcrListItemDto(n.Id, n.NcrNo, n.Status, n.Source, n.DefectCode, n.Description, n.Qty, n.Targets.Count,
                n.Disposition, n.CreatedBy, n.CreatedAt)).ToList());
    }

    public Task<NcrDto> GetAsync(int id, CancellationToken ct = default) => Task.FromResult(Store[id]);

    public Task<NcrDto> CreateAsync(CreateNcrRequest request, CancellationToken ct = default)
    {
        Created = request;
        return Change(99, _ => Ncr(99, "Open"));
    }

    public Task<NcrDto> StartReviewAsync(int id, string rowVersion, CancellationToken ct = default)
        => Change(id, n => n with { Status = "UnderReview" }, $"review {rowVersion}");

    public Task<NcrDto> ProposeAsync(int id, ProposeDispositionRequest request, CancellationToken ct = default)
        => Change(id, n => n with { Disposition = request.Disposition, DispositionNote = request.Note, ProposedBy = "qc01" },
            $"propose {request.Disposition} {request.Note} {request.ConcessionRef}");

    public Task<NcrDto> ApproveAsync(int id, string rowVersion, CancellationToken ct = default)
        => Change(id, n => n with { Status = "Dispositioned", ApprovedBy = "sup01" }, "approve");

    public Task<NcrDto> RejectProposalAsync(int id, string rowVersion, CancellationToken ct = default)
        => Change(id, n => n with { Disposition = null, ProposedBy = null }, "reject");

    public Task<NcrDto> CloseAsync(int id, CloseNcrRequest request, CancellationToken ct = default)
        => Change(id, n => n with { Status = "Closed", CapaReference = request.CapaReference }, $"close {request.CapaReference}");

    public Task<NcrDto> CancelAsync(int id, CancelNcrRequest request, CancellationToken ct = default)
        => Change(id, n => n with { Status = "Cancelled", CancelReason = request.Reason }, $"cancel {request.Reason}");

    private Task<NcrDto> Change(int id, Func<NcrDto, NcrDto> change, string? call = null)
    {
        if (call is not null) Calls.Add(call);
        if (Fail is { } fail) { Fail = null; throw fail; }
        var next = change(Store.GetValueOrDefault(id) ?? Ncr(id, "Open")) with { RowVersion = $"v{++_version}" };
        Store[id] = next;
        return Task.FromResult(next);
    }
}

internal sealed class DefectCodesApi : FakeMasterDataApi
{
    public override Task<IReadOnlyList<DefectCodeDto>> GetDefectCodesAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<DefectCodeDto>>([new(1, "MEAS-OOS", "Out of spec", "Functional", true),
            new(2, "OLD-CODE", "Retired", "Other", false)]);
}

internal sealed class AnswerDialogs(bool confirm = true, string? text = null) : IDialogService
{
    public List<string> Confirms { get; } = [];
    public bool Confirm(string message) { Confirms.Add(message); return confirm; }
    public string? PickFileToOpen(string filter) => null;
    public string? PickFileToSave(string defaultFileName, string filter) => null;
    public string? AskText(string title, string prompt) => text;
}

// Spec M7 step 8: the NCR screen. Buttons follow status and role; the API (fake here) has the last word.
public class NcrViewModelTests
{
    private readonly FakeNcrApi _api = new();

    private NcrViewModel Vm(string user, string role, IDialogService? dialogs = null)
    {
        var tokens = new TokenStore();
        tokens.Set(new LoginResponse("t", DateTime.UtcNow.AddHours(1), user, user, [role], null));
        return new NcrViewModel(_api, new DefectCodesApi(), tokens, dialogs ?? new AnswerDialogs());
    }

    private async Task<NcrViewModel> ShowingAsync(NcrDto ncr, string user, string role, IDialogService? dialogs = null)
    {
        _api.Store[ncr.Id] = ncr;
        var vm = Vm(user, role, dialogs);
        await vm.LoadAsync();
        vm.SelectedRow = vm.Ncrs.Single(n => n.Id == ncr.Id);
        await Task.Yield();
        return vm;
    }

    [Fact]
    public async Task Loads_the_list_and_only_active_defect_codes()
    {
        _api.Store[1] = FakeNcrApi.Ncr(1, "Open");
        var vm = Vm("qc01", "QC");

        await vm.LoadAsync();

        Assert.Equal(["MEAS-OOS"], vm.DefectCodes);
        Assert.Equal([1], vm.Ncrs.Select(n => n.Id));
        vm.StatusFilter = "Closed";
        Assert.Equal("list Closed", _api.Calls[^1]);
        Assert.Empty(vm.Ncrs);
    }

    // Step check, on the screen: QC reviews and proposes, a Supervisor approves, then closes.
    [Fact]
    public async Task Qc_proposes_and_a_supervisor_approves_and_closes()
    {
        var qc = await ShowingAsync(FakeNcrApi.Ncr(1, "Open"), "qc01", "QC");
        Assert.Equal((true, false, false), (qc.CanStartReview, qc.CanApprove, qc.CanClose));

        await qc.StartReviewCommand.ExecuteAsync(null);
        Assert.True(qc.CanPropose);
        (qc.ProposedDisposition, qc.DispositionNote) = ("Rework", "Reflow U3");
        await qc.ProposeCommand.ExecuteAsync(null);
        Assert.False(qc.CanApprove);                                         // QC has no approver role

        var sup = await ShowingAsync(_api.Store[1], "sup01", "Supervisor");
        Assert.Equal((false, true, true), (sup.CanPropose, sup.CanApprove, sup.CanReject));
        await sup.ApproveCommand.ExecuteAsync(null);
        sup.CapaReference = " CAPA-26-014 ";
        Assert.True(sup.CanClose);
        await sup.CloseNcrCommand.ExecuteAsync(null);

        Assert.Equal(["review v0", "propose Rework Reflow U3 ", "approve", "close CAPA-26-014"], _api.Calls.Where(c => !c.StartsWith("list")));
        Assert.Equal("Closed", sup.Detail!.Status);
        Assert.Equal((false, false, false, false), (sup.CanApprove, sup.CanClose, sup.CanCancel, sup.CanPropose));
    }

    // Q11 on the screen: an Admin (both roles) who proposed sees Approve off and why.
    [Fact]
    public async Task The_proposer_sees_approve_off_with_the_reason()
    {
        var vm = await ShowingAsync(FakeNcrApi.Ncr(1, "UnderReview", "Scrap", proposedBy: "ADMIN"), "admin", "Admin");

        Assert.False(vm.CanApprove);
        Assert.False(vm.ApproveCommand.CanExecute(null));
        Assert.True(vm.CanReject);
        Assert.NotEmpty(vm.ApproveHint);
    }

    // Q12 on the screen: Close stays off while holds of the NCR are active.
    [Fact]
    public async Task Close_is_off_while_holds_are_active()
    {
        var vm = await ShowingAsync(FakeNcrApi.Ncr(1, "Dispositioned", "UseAsIs", "qc01", holds: 2), "sup01", "Supervisor");
        Assert.False(vm.CanClose);
        Assert.Contains("2", vm.ApproveHint);
    }

    [Fact]
    public async Task Approve_asks_first_and_cancel_needs_a_reason()
    {
        var vm = await ShowingAsync(FakeNcrApi.Ncr(1, "UnderReview", "Scrap", "qc01"), "sup01", "Supervisor", new AnswerDialogs(confirm: false));
        await vm.ApproveCommand.ExecuteAsync(null);
        Assert.DoesNotContain("approve", _api.Calls);

        var qc = await ShowingAsync(FakeNcrApi.Ncr(2, "Open"), "qc01", "QC", new AnswerDialogs(text: " "));
        await qc.CancelNcrCommand.ExecuteAsync(null);
        Assert.DoesNotContain(_api.Calls, c => c.StartsWith("cancel"));

        var qc2 = await ShowingAsync(FakeNcrApi.Ncr(3, "Open"), "qc01", "QC", new AnswerDialogs(text: "Duplicate of NCR-000002"));
        await qc2.CancelNcrCommand.ExecuteAsync(null);
        Assert.Equal("Cancelled", qc2.Detail!.Status);
    }

    // 409: someone else changed it; the screen reloads what is true now and says so.
    [Fact]
    public async Task A_conflict_reloads_the_ncr_and_shows_the_message()
    {
        var vm = await ShowingAsync(FakeNcrApi.Ncr(1, "Open"), "qc01", "QC");
        _api.Store[1] = _api.Store[1] with { Status = "Cancelled", RowVersion = "v9" };
        _api.Fail = new ApiException(HttpStatusCode.Conflict, "NCR NCR-000001 was changed by someone else.");

        await vm.StartReviewCommand.ExecuteAsync(null);

        Assert.Equal(("Cancelled", "v9"), (vm.Detail!.Status, vm.Detail.RowVersion));
        Assert.Contains("someone else", vm.ErrorMessage);
        Assert.False(vm.CanStartReview);
    }

    [Fact]
    public async Task A_new_ncr_sends_serials_and_lots_from_the_text_boxes()
    {
        var vm = Vm("qc01", "QC");
        await vm.LoadAsync();
        vm.NewFormCommand.Execute(null);
        (vm.NewDescription, vm.NewQty, vm.NewDefectCode) = ("Bridge on U3", 2, "MEAS-OOS");
        vm.NewSerials = "PCBA1\r\npcba1\nPCBA2";
        vm.NewLots = "L-CAP-01; ";

        await vm.CreateCommand.ExecuteAsync(null);

        Assert.Equal([("Serial", "PCBA1"), ("Serial", "PCBA2"), ("Lot", "L-CAP-01")], _api.Created!.Targets.Select(t => (t.Type, t.Code)));
        Assert.Equal(("Manual", null, 2), (_api.Created.Source, _api.Created.SpcAlertId, _api.Created.Qty));
        Assert.Contains("NCR-000099", vm.InfoMessage);
        Assert.False(vm.IsCreating);
    }

    // "Create NCR" on the chart: the NCR screen opens with the form filled from the alert and its board.
    [Fact]
    public async Task Create_ncr_from_an_alert_fills_the_form()
    {
        var vm = Vm("qc01", "QC");
        var alert = new SpcAlertDto(17, 1, "3V3_RAIL", "WE4", 106, DateTime.UtcNow, "Open", null);

        await vm.StartFromAlertAsync(alert, "WE4 on 3V3_RAIL", "PCBA6");

        Assert.Equal((true, "SpcAlert", "17", (long?)17, "WE4 on 3V3_RAIL", "PCBA6"),
            (vm.IsCreating, vm.NewSource, vm.NewSourceRef, vm.NewSpcAlertId, vm.NewDescription, vm.NewSerials));
    }
}
