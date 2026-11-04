using System.Net;
using Microsoft.Extensions.Time.Testing;
using MiniMes.Client.Core.Auth;
using MiniMes.Client.Core.Http;
using MiniMes.Client.Core.Resources;
using MiniMes.Client.Core.ViewModels.Quality;
using MiniMes.Contracts.Auth;
using MiniMes.Contracts.Quality;

namespace MiniMes.UnitTests.Client;

// Spec M7 step 9: the Hold panel (preview first, reason required, what is held is what was shown) and the Holds screen.
public class HoldViewModelTests
{
    private static readonly DateTime T0 = new(2026, 11, 4, 1, 0, 0, DateTimeKind.Utc);
    private readonly FakeHoldApi _api = new();
    private readonly FakeTimeProvider _time = new(T0);

    public HoldViewModelTests() => _time.SetLocalTimeZone(TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh"));

    private HoldPanelViewModel Panel() => new(_api, _time);

    [Fact]
    public async Task The_preview_says_what_will_be_held_and_warns_about_shipped_boards()
    {
        var panel = Panel();

        await panel.OpenAsync("Lot", "L-CAP-B");

        Assert.Equal("87 boards (8 Completed, 79 InProcess). Lots held: L-CAP-A, L-CAP-B. Lots left as they are: L-CAP-P (Consumed).", panel.PreviewText);
        Assert.Equal("3 of them are already shipped: notify the customer. 1 scrapped boards are left out.", panel.WarningText);
    }

    [Fact]
    public async Task Hold_needs_a_reason_and_sends_what_was_previewed()
    {
        var panel = Panel();
        HoldResultDto? held = null;
        panel.Held += r => held = r;
        await panel.OpenAsync("Lot", "L-CAP-B");
        Assert.False(panel.HoldCommand.CanExecute(null));

        panel.Reason = " Supplier recall ";
        panel.CreateNcr = true;
        await panel.HoldCommand.ExecuteAsync(null);

        var sent = Assert.Single(_api.Holds);
        Assert.Equal(("Lot", "L-CAP-B", "Supplier recall", true), (sent.Scope, sent.LotNo, sent.Reason, sent.CreateNcr));
        Assert.Equal("Held: 87 boards, 2 lots. NCR NCR-000009 opened.", panel.ResultMessage);
        Assert.Equal("NCR-000009", held!.NcrNo);
        Assert.False(panel.HoldCommand.CanExecute(null));   // done: a new hold needs a new preview
    }

    // Changing the target after the preview: Hold is off until the new target is previewed.
    [Fact]
    public async Task Changing_the_target_clears_the_preview()
    {
        var panel = Panel();
        await panel.OpenAsync("Lot", "L-CAP-B");
        panel.Reason = "x";
        Assert.True(panel.HoldCommand.CanExecute(null));

        panel.Reference = "L-CAP-C";

        Assert.False(panel.HoldCommand.CanExecute(null));
        Assert.Equal(string.Empty, panel.PreviewText);
    }

    // Plant time in, UTC out; serials one per line (or with commas / spaces).
    [Fact]
    public async Task Window_times_are_plant_time_and_serials_are_split()
    {
        var panel = Panel();
        await panel.OpenAsync("MachineWindow", "L1-REFLOW-01", T0, T0.AddHours(1));
        Assert.Equal(("2026-11-04 08:00", "2026-11-04 09:00"), (panel.FromText, panel.ToText));
        panel.ToText = "2026-11-04 09:30";
        await panel.PreviewCommand.ExecuteAsync(null);
        panel.ToText = "tomorrow";
        await panel.PreviewCommand.ExecuteAsync(null);

        Assert.Equal(T0.AddMinutes(90), _api.Previews[^1].To);
        Assert.Equal(string.Format(Strings.Hold_BadTime, HoldPanelViewModel.TimeFormat), panel.ErrorMessage);

        await panel.OpenAsync("Serials", "PCBA1\r\nPCBA2, PCBA3 PCBA1", canChooseScope: true);
        Assert.Equal(["PCBA1", "PCBA2", "PCBA3"], _api.Previews[^1].SerialNos!);
    }

    [Fact]
    public async Task A_refused_preview_shows_the_field_message()
    {
        _api.Fail = new ApiException(HttpStatusCode.BadRequest, "Validation failed",
            fieldErrors: new Dictionary<string, string[]> { ["SerialNos"] = ["Not found: PCBA9."] });
        var panel = Panel();

        await panel.OpenAsync("Serials", "PCBA9");

        Assert.Equal("Not found: PCBA9.", panel.ErrorMessage);
        Assert.False(panel.HasPreview);
    }

    // ---------- the Holds screen ----------

    private HoldsViewModel Screen(string role, string? note)
    {
        var tokens = new TokenStore();
        tokens.Set(new LoginResponse("t", T0.AddHours(1), "sup01", "sup01", [role], null));
        return new HoldsViewModel(_api, tokens, new AnswerDialogs(text: note), _time, Panel());
    }

    private static HoldBatchDto Batch() => new(Guid.NewGuid(), "Lot", "L-CAP-B", "Supplier recall", 9, "NCR-000009", 87, 2, 0, "qc01", T0);

    [Fact]
    public async Task Release_asks_for_a_note_and_says_what_stayed_held()
    {
        _api.Batches.Add(Batch());
        var noNote = Screen("Supervisor", note: " ");
        await noNote.LoadAsync();
        noNote.Selected = noNote.Batches[0];
        await noNote.ReleaseCommand.ExecuteAsync(null);
        Assert.Empty(_api.Releases);

        var vm = Screen("Supervisor", note: "Supplier confirmed: label error only");
        await vm.LoadAsync();
        Assert.Equal(("04/11 08:00", "87 boards, 2 lots"), (vm.Batches[0].HeldAt, vm.Batches[0].Targets));
        vm.Selected = vm.Batches[0];
        await vm.ReleaseCommand.ExecuteAsync(null);

        Assert.Equal("Supplier confirmed: label error only", _api.Releases.Single().Note);
        Assert.Equal("Released 5 targets: 4 back to their status before the hold, 1 still held by another hold.", vm.InfoMessage);
    }

    [Fact]
    public async Task A_planner_sees_the_holds_but_cannot_release_or_hold()
    {
        _api.Batches.Add(Batch());
        var vm = Screen("Planner", note: "x");
        await vm.LoadAsync();
        vm.Selected = vm.Batches[0];

        Assert.False(vm.CanAct);
        Assert.False(vm.ReleaseCommand.CanExecute(null));
    }
}
