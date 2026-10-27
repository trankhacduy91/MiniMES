using Microsoft.Extensions.Time.Testing;
using MiniMes.Client.Core.Auth;
using MiniMes.Client.Core.Resources;
using MiniMes.Client.Core.ViewModels.Machines;
using MiniMes.Contracts.Auth;
using MiniMes.Contracts.Machines;
using MiniMes.Contracts.Realtime;
using static MiniMes.UnitTests.Client.FakeMachineApi;
using WireState = MiniMes.Contracts.Machines.MachineState;

namespace MiniMes.UnitTests.Client;

// Spec M4 step 8: snapshot + pushes on the line dashboard, without a server. The fake realtime client lets each test
// deliver pushes in the order it wants, including the awkward ones (late, other line, after a reconnect).
public class LineDashboardViewModelTests
{
    private readonly FakeMachineApi _api = new();
    private readonly FakeRealtimeFactory _rt = new();
    private readonly TokenStore _tokens = new();
    private FakeRealtimeClient Rt => _rt.Client;

    public LineDashboardViewModelTests()
    {
        _api.Status["L1"] = new LineStatusDto("L1", [Machine(1, "L1-SMT-01"), Machine(2, "L1-REFLOW-01", source: "OpcUa")]);
        _api.Status["L2"] = new LineStatusDto("L2", [Machine(9, "L2-SMT-01")]);
        _api.Open["L1"] = [Call(5, 1, "L1-SMT-01", "Machine", "Raised", 100)];
        _tokens.Set(new LoginResponse("t", T0, "sup01", "Supervisor One", ["Supervisor"], null));
    }

    private LineDashboardViewModel Vm() => new(_api, _rt, new ImmediateDispatcher(), _tokens, new FakeTimeProvider(T0));

    private async Task<LineDashboardViewModel> LoadedAsync()
    {
        var vm = Vm();
        await vm.LoadAsync();
        return vm;
    }

    private static MachineTileViewModel Tile(LineDashboardViewModel vm, string code) => vm.Tiles.Single(t => t.Code == code);

    [Fact]
    public async Task Load_joins_the_default_line_before_reading_the_snapshot_then_connects()
    {
        var joinedFirst = false;
        _api.OnSnapshot = line => joinedFirst = Rt.Joined.Contains(line);

        var vm = await LoadedAsync();

        Assert.True(joinedFirst);
        Assert.True(Rt.Started);
        Assert.Equal("L1", vm.SelectedLine!.Code);
        Assert.Equal(["L1-SMT-01", "L1-REFLOW-01"], vm.Tiles.Select(t => t.Code));
        Assert.Equal(("MQTT", "OPC UA"), (Tile(vm, "L1-SMT-01").SourceLabel, Tile(vm, "L1-REFLOW-01").SourceLabel));
        Assert.Equal(string.Format(Strings.Dashboard_ThisHour, 10), Tile(vm, "L1-SMT-01").HourText);
        Assert.Equal(5, Assert.Single(vm.Andons).Id);
        Assert.Single(vm.DowntimeReasons);
    }

    [Fact]
    public async Task State_push_recolours_the_tile_but_a_late_or_foreign_push_does_not()
    {
        var vm = await LoadedAsync();

        Rt.Push(new MachineStateChangedDto("L1-SMT-01", "L1", WireState.Down, T0.AddMinutes(1)));
        Rt.Push(new MachineStateChangedDto("L1-SMT-01", "L1", WireState.Idle, T0.AddSeconds(30)));    // older than shown
        Rt.Push(new MachineStateChangedDto("L1-SMT-01", "L2", WireState.Offline, T0.AddMinutes(2)));  // not this line

        Assert.Equal((WireState.Down, "Down"), (Tile(vm, "L1-SMT-01").State, Tile(vm, "L1-SMT-01").StateText));
    }

    [Fact]
    public async Task Counter_and_process_pushes_replace_the_values_shown()
    {
        var vm = await LoadedAsync();

        Rt.Push(new MachineCounterDto("L1-SMT-01", "L1", 130, 40, T0.AddMinutes(2)));
        Rt.Push(new MachineCounterDto("L1-SMT-01", "L1", 120, 30, T0.AddMinutes(1)));   // arrived late: ignored
        Rt.Push(new MachineProcessDto("L1-REFLOW-01", "L1", "peakTemp", "°C", 245.3, DataQuality.Good, T0));
        Rt.Push(new MachineProcessDto("L1-REFLOW-01", "L1", "peakTemp", "°C", -32768, DataQuality.Bad, T0.AddSeconds(5)));

        Assert.Equal((130L, 40L), (Tile(vm, "L1-SMT-01").Counter, Tile(vm, "L1-SMT-01").CountThisHour));
        var reading = Assert.Single(Tile(vm, "L1-REFLOW-01").Process);
        Assert.Equal(("peakTemp -32768 °C", true), (reading.Text, reading.IsBad));   // Bad is shown greyed, not hidden
    }

    [Fact]
    public async Task Andon_pushes_add_update_and_remove_rows_and_never_go_back()
    {
        var vm = await LoadedAsync();

        Rt.Push(Call(6, 2, "L1-REFLOW-01", "Quality", "Raised", 200));
        Rt.Push(Call(5, 1, "L1-SMT-01", "Machine", "Acknowledged", 110, ackBy: "mt01"));
        Rt.Push(Call(5, 1, "L1-SMT-01", "Machine", "Raised", 100, level: 1));    // older than the ack: ignored
        Assert.Equal(["Acknowledged", "Raised"], vm.Andons.OrderBy(a => a.Id).Select(a => a.Status));
        Assert.Equal(string.Format(Strings.Dashboard_AckBy, "mt01"), vm.Andons.Single(a => a.Id == 5).AckText);

        Rt.Push(Call(6, 2, "L1-REFLOW-01", "Quality", "Resolved", 210));
        Rt.Push(Call(6, 2, "L1-REFLOW-01", "Quality", "Raised", 200));           // late: must not bring it back
        Rt.Push(Call(7, 9, "L2-SMT-01", "Machine", "Raised", 300, line: "L2"));  // other line

        Assert.Equal([5L], vm.Andons.Select(a => a.Id));
    }

    // [v2] Level >= 1 makes the row blink (IsEscalated) and the escalation push shows a banner.
    [Fact]
    public async Task Escalation_marks_the_row_and_shows_a_banner()
    {
        var vm = await LoadedAsync();
        var escalated = Call(5, 1, "L1-SMT-01", "Machine", "Raised", 120, level: 1);

        Rt.Escalate(escalated);
        Rt.Push(escalated);

        Assert.True(vm.Andons.Single().IsEscalated);
        Assert.Contains("L1-SMT-01", vm.EscalationAlert);
        vm.DismissEscalationCommand.Execute(null);
        Assert.Empty(vm.EscalationAlert);
    }

    // After a reconnect the snapshot is the truth: what changed while the client was away is shown.
    [Fact]
    public async Task Reconnect_shows_the_banner_then_reloads_the_snapshot()
    {
        var vm = await LoadedAsync();

        Rt.Drop();
        Assert.True(vm.IsReconnecting);
        _api.Status["L1"] = new LineStatusDto("L1", [Machine(1, "L1-SMT-01", WireState.Down, T0.AddMinutes(5)), Machine(2, "L1-REFLOW-01")]);
        _api.Open["L1"] = [];
        await Rt.ReconnectAsync();

        Assert.False(vm.IsReconnecting);
        Assert.Equal(2, _api.SnapshotLoads);
        Assert.Equal(WireState.Down, Tile(vm, "L1-SMT-01").State);
        Assert.Empty(vm.Andons);
    }

    [Fact]
    public async Task Switching_line_leaves_the_old_group_and_shows_the_new_line()
    {
        var vm = await LoadedAsync();

        vm.SelectedLine = L2;
        await Task.Yield();

        Assert.Equal(["L1"], Rt.Left);
        Assert.Equal(["L1", "L2"], Rt.Joined);
        Assert.Equal(["L2-SMT-01"], vm.Tiles.Select(t => t.Code));
        Rt.Push(new MachineStateChangedDto("L1-SMT-01", "L1", WireState.Down, T0.AddHours(1)));   // old line: nothing
        Assert.Equal(WireState.Running, Tile(vm, "L2-SMT-01").State);
    }

    [Fact]
    public async Task Acknowledge_applies_the_answer()
    {
        _api.Acknowledge = a => a with { Status = "Acknowledged", AckBy = "sup01", RowVersion = Version(150) };
        var vm = await LoadedAsync();

        await vm.AcknowledgeCommand.ExecuteAsync(vm.Andons.Single());

        Assert.Equal(("Acknowledged", ""), (vm.Andons.Single().Status, vm.ErrorMessage));
    }

    // Two supervisors: the slower one gets 409, is told why, and sees the call as it now is.
    [Fact]
    public async Task Acknowledge_conflict_shows_the_message_and_reloads()
    {
        _api.Acknowledge = _ => throw Conflict();
        var vm = await LoadedAsync();
        _api.Open["L1"] = [Call(5, 1, "L1-SMT-01", "Machine", "Acknowledged", 110, ackBy: "mt01")];

        await vm.AcknowledgeCommand.ExecuteAsync(vm.Andons.Single());

        Assert.Contains("someone else", vm.ErrorMessage);
        Assert.Equal("Acknowledged", vm.Andons.Single().Status);
    }

    [Fact]
    public async Task Resolve_needs_a_reason_then_removes_the_row()
    {
        var vm = await LoadedAsync();
        var row = vm.Andons.Single();

        await vm.ResolveCommand.ExecuteAsync(row);
        Assert.Equal(Strings.Dashboard_ChooseReason, vm.ErrorMessage);
        Assert.Empty(_api.Resolved);

        row.SelectedReason = Fault;
        await vm.ResolveCommand.ExecuteAsync(row);

        Assert.Equal([(5L, 6)], _api.Resolved);
        Assert.Empty(vm.Andons);
    }

    [Theory]
    [InlineData("Supervisor", true)]
    [InlineData("Maintenance", true)]
    [InlineData("Admin", true)]
    [InlineData("Planner", false)]
    [InlineData("QC", false)]
    public void Only_responders_see_the_answer_buttons(string role, bool canRespond)
    {
        _tokens.Set(new LoginResponse("t", T0, "u", "U", [role], null));

        Assert.Equal(canRespond, Vm().CanRespond);
    }

    [Fact]
    public async Task Api_down_at_load_shows_the_error()
    {
        _api.Fails = new MiniMes.Client.Core.Http.ApiException(null, Strings.Error_CannotReachServer);

        var vm = await LoadedAsync();

        Assert.Equal(Strings.Error_CannotReachServer, vm.ErrorMessage);
        Assert.Empty(vm.Tiles);
    }

    // Leaving the page closes the connection (IClosable), and no handler is left to reload a dead screen.
    [Fact]
    public async Task Close_disposes_the_connection()
    {
        var vm = await LoadedAsync();

        await vm.CloseAsync();

        Assert.True(Rt.Disposed);
        Assert.False(Rt.HasResyncHandler);
    }
}
