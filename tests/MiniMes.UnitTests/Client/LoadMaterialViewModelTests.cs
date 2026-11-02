using System.Net;
using MiniMes.Client.Core.Http;
using MiniMes.Client.Core.Kiosk;
using MiniMes.Client.Core.Resources;
using MiniMes.Client.Core.ViewModels.Kiosk;
using MiniMes.Contracts.Material;

namespace MiniMes.UnitTests.Client;

// Spec M6 step 4: the kiosk "Load material" screen without a server. Everything enters through the scan box: a reel
// label, then (after a FIFO warning and a tapped reason) the supervisor's badge, or the count left on a reel taken off.
public class LoadMaterialViewModelTests
{
    private static readonly DateTime At = new(2026, 10, 19, 1, 0, 0, DateTimeKind.Utc);

    private static StationMaterialDto Row(int itemId, string item, string? lot, int? loadId = null)
        => new(itemId, item, item, "pcs", true, 4, loadId, lot, lot is null ? null : 3_200, null, lot is null ? null : "EMP00123", lot is null ? null : At, null);

    private static LoadMaterialResponse Answer(string outcome, string code, string? suggested = null, StationMaterialDto? loaded = null)
        => new(outcome, code, "server words", suggested, loaded);

    private readonly FakeKiosk _kiosk = new FakeKiosk().Started();

    private async Task<LoadMaterialViewModel> LoadedAsync()
    {
        _kiosk.Material.Checklist.AddRange([Row(1, "CAP-100N-0402", null), Row(2, "RES-100K-0603", "L-260901-000002", loadId: 9)]);
        var vm = _kiosk.LoadMaterial();
        await vm.LoadAsync();
        return vm;
    }

    private static async Task ScanAsync(LoadMaterialViewModel vm, string text)
    {
        vm.ScanInput = text;
        await vm.SubmitCommand.ExecuteAsync(null);
    }

    [Fact]
    public async Task Checklist_flags_what_is_not_loaded()
    {
        var vm = await LoadedAsync();

        Assert.Equal([("CAP-100N-0402", true, false), ("RES-100K-0603", false, true)],
            vm.Materials.Select(m => (m.Material.ItemCode, m.IsMissing, m.CanUnload)));
        Assert.Equal(Strings.Material_PromptLot, vm.Prompt);
    }

    [Fact]
    public async Task Right_reel_is_green_and_the_checklist_reloads()
    {
        var vm = await LoadedAsync();
        _kiosk.Material.Answer = r =>
        {
            _kiosk.Material.Checklist[0] = Row(1, "CAP-100N-0402", r.LotNo, loadId: 10);
            return Task.FromResult(Answer("Accepted", "Ok", loaded: _kiosk.Material.Checklist[0]));
        };

        await ScanAsync(vm, " l-260915-000001 ");

        Assert.Equal(new LoadMaterialRequest("L-260915-000001"), _kiosk.Material.Loads.Single());   // trimmed, upper case
        Assert.Equal((ScanState.Ok, Strings.Material_Loaded), (vm.State, vm.Headline));
        Assert.False(vm.Materials[0].IsMissing);
        Assert.Equal([ScanState.Ok], _kiosk.Feedback.Played);
    }

    // 1.3 A #4: the wrong resistor -> red, the translated reason, the server's words as detail. No override offered.
    [Fact]
    public async Task Wrong_reel_is_red_with_the_reason()
    {
        var vm = await LoadedAsync();
        _kiosk.Material.Answer = _ => Task.FromResult(Answer("Rejected", "ItemNotInBom"));

        await ScanAsync(vm, "L-260801-000004");

        Assert.Equal((ScanState.Blocked, Strings.Material_Rejected, Strings.Load_ItemNotInBom), (vm.State, vm.Headline, vm.Message));
        Assert.Equal("L-260801-000004 - server words", vm.Detail);
        Assert.False(vm.IsOverrideOpen);
    }

    // 1.3 A #3: yellow -> tap a reason -> the supervisor scans the badge -> the same reel is sent again with both.
    [Fact]
    public async Task Fifo_warning_is_approved_with_a_reason_and_a_supervisor_badge()
    {
        var vm = await LoadedAsync();
        _kiosk.Material.Answer = r => Task.FromResult(r.SupervisorBadge is null
            ? Answer("Warning", "FifoOlderLotAvailable", suggested: "L-260901-000002")
            : Answer("Accepted", "FifoOlderLotAvailable", loaded: Row(1, "CAP-100N-0402", r.LotNo, 10) with { FifoOverrideBy = "sup01" }));

        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        await ScanAsync(vm, "L-260915-000001");
        Assert.Equal((ScanState.Warning, true), (vm.State, vm.IsOverrideOpen));
        Assert.Contains(nameof(vm.Prompt), changed);   // the screen re-reads it (found on the real kiosk: it did not)
        Assert.Equal(string.Format(Strings.Material_FifoPanel, "L-260901-000002", "L-260915-000001"), vm.OverrideText);
        Assert.Equal(Strings.Material_PromptReason, vm.Prompt);

        vm.ChooseReasonCommand.Execute(Strings.Material_ReasonOlderSuspect);
        Assert.Equal((MaterialInput.SupervisorBadge, string.Format(Strings.Material_PromptBadge, Strings.Material_ReasonOlderSuspect)), (vm.Input, vm.Prompt));
        await ScanAsync(vm, "sup01");

        Assert.Equal(new LoadMaterialRequest("L-260915-000001", null, "SUP01", Strings.Material_ReasonOlderSuspect), _kiosk.Material.Loads[^1]);
        Assert.Equal((ScanState.Ok, string.Format(Strings.Material_LoadedApproved, "CAP-100N-0402", "sup01")), (vm.State, vm.Message));
        Assert.Equal((false, MaterialInput.Lot), (vm.IsOverrideOpen, vm.Input));
    }

    // The operator follows the advice and scans the older reel instead: the open question is dropped.
    [Fact]
    public async Task Scanning_another_reel_drops_the_fifo_question()
    {
        var vm = await LoadedAsync();
        _kiosk.Material.Answer = r => Task.FromResult(r.LotNo == "L-260915-000001"
            ? Answer("Warning", "FifoOlderLotAvailable", suggested: "L-260901-000003")
            : Answer("Accepted", "Ok", loaded: Row(1, "CAP-100N-0402", r.LotNo, 11)));
        await ScanAsync(vm, "L-260915-000001");

        await ScanAsync(vm, "L-260901-000003");

        Assert.Equal(new LoadMaterialRequest("L-260901-000003"), _kiosk.Material.Loads[^1]);
        Assert.False(vm.IsOverrideOpen);
        Assert.Equal(ScanState.Ok, vm.State);
    }

    // An operator's badge is not a supervisor's: the server says 400 on SupervisorBadge; red with its words, and the
    // warning stays open for a real supervisor.
    [Fact]
    public async Task Badge_that_is_not_a_supervisor_is_refused()
    {
        var vm = await LoadedAsync();
        _kiosk.Material.Answer = r => r.SupervisorBadge is null
            ? Task.FromResult(Answer("Warning", "FifoOlderLotAvailable", suggested: "L-260901-000002"))
            : Task.FromException<LoadMaterialResponse>(new ApiException(HttpStatusCode.BadRequest, "x",
                fieldErrors: new Dictionary<string, string[]> { ["SupervisorBadge"] = ["'EMP00123' is not an active supervisor's badge."] }));
        await ScanAsync(vm, "L-260915-000001");
        vm.ChooseReasonCommand.Execute(Strings.Material_ReasonCustomer);

        await ScanAsync(vm, "EMP00123");

        Assert.Equal((ScanState.Blocked, "'EMP00123' is not an active supervisor's badge."), (vm.State, vm.Message));
        Assert.True(vm.IsOverrideOpen);
        Assert.Equal(MaterialInput.SupervisorBadge, vm.Input);
    }

    // R10: take a reel off; the count left is typed in the scan box (numpad + Enter), or Empty = 0.
    [Fact]
    public async Task Unload_sends_the_count_typed_or_zero()
    {
        var vm = await LoadedAsync();
        vm.StartUnloadCommand.Execute(vm.Materials[1]);
        Assert.Equal((MaterialInput.Remaining, true), (vm.Input, vm.IsUnloadOpen));

        await ScanAsync(vm, "abc");
        Assert.Empty(_kiosk.Material.Unloads);
        Assert.Equal(Strings.Material_RemainingNumber, vm.Message);

        await ScanAsync(vm, "1840");
        Assert.Equal([(9, 1840m)], _kiosk.Material.Unloads);
        Assert.Equal((MaterialInput.Lot, false), (vm.Input, vm.IsUnloadOpen));

        vm.StartUnloadCommand.Execute(vm.Materials[1]);
        await vm.UnloadEmptyCommand.ExecuteAsync(null);
        Assert.Equal((9, (decimal?)0m), _kiosk.Material.Unloads[^1]);
    }

    [Fact]
    public async Task Back_returns_to_scanning_and_the_scan_screen_opens_material()
    {
        var vm = await LoadedAsync();
        vm.BackCommand.Execute(null);
        Assert.Equal("Scan", _kiosk.Navigation.Last);

        _kiosk.Scan().ShowMaterialCommand.Execute(null);
        Assert.Equal("Material", _kiosk.Navigation.Last);
    }
}
