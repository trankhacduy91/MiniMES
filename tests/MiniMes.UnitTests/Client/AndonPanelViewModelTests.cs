using MiniMes.Client.Core.Kiosk;
using MiniMes.Client.Core.Resources;
using MiniMes.Client.Core.ViewModels.Kiosk;
using MiniMes.Contracts.Andon;
using MiniMes.Contracts.Factory;
using MiniMes.Contracts.MasterData;
using MiniMes.Contracts.Realtime;
using static MiniMes.UnitTests.Client.FakeMachineApi;
using WireState = MiniMes.Contracts.Machines.MachineState;

namespace MiniMes.UnitTests.Client;

// Spec M4 step 8, kiosk: the Andon button and the banner of the station's machines. A station reaches its machines
// through its work center (decision 27/10/2026): one machine is preselected, several are chosen, none = no Andon.
public class AndonPanelViewModelTests
{
    private static readonly StationDto Smt = new(1, LineId: 1, "L1-SMT-01", "SMT", "SMT", true, WorkCenterId: 10);
    private static readonly StationDto Test = new(4, LineId: 1, "L1-TEST-01", "Test", "Test", true, WorkCenterId: 30);
    private static readonly StationDto Pack = new(5, LineId: 1, "L1-PACK-01", "Pack", "Pack", true, WorkCenterId: 40);

    private readonly FakeMachineApi _api = new();
    private readonly FakeRealtimeFactory _rt = new();
    private readonly KioskContext _kiosk = new();
    private FakeRealtimeClient Rt => _rt.Client;

    public AndonPanelViewModelTests()
    {
        _api.MachinesByWorkCenter[10] = [Dto(1, "L1-SMT-01"), Dto(2, "L1-REFLOW-01")];
        _api.MachinesByWorkCenter[30] = [Dto(4, "L1-TEST-01")];
        _api.Status["L1"] = new LineStatusDto("L1", [Machine(1, "L1-SMT-01"), Machine(2, "L1-REFLOW-01", WireState.Down), Machine(4, "L1-TEST-01")]);
        _api.Open["L1"] = [Call(5, 2, "L1-REFLOW-01", "Machine", "Raised", 100)];
    }

    private static MachineDto Dto(int id, string code) => new(id, 10, code, code + " name", "Type", code, "Mqtt", true);

    private async Task<AndonPanelViewModel> StartedAsync(StationDto station)
    {
        _kiosk.SetStation(station);
        var vm = new AndonPanelViewModel(_api, _rt, new ImmediateDispatcher(), _kiosk);
        await vm.StartAsync();
        return vm;
    }

    [Fact]
    public async Task One_machine_is_preselected_and_its_state_shown()
    {
        var vm = await StartedAsync(Test);

        Assert.Equal(("L1-TEST-01", false, true), (vm.SelectedMachine?.Code, vm.MustChooseMachine, vm.HasMachines));
        Assert.Equal("Running", vm.Machines.Single().StateText);
        Assert.Equal(["L1"], Rt.Joined);
        Assert.True(Rt.Started);
    }

    // Two machines on the work center: the operator says which one; nothing can be sent before that.
    [Fact]
    public async Task Several_machines_must_be_chosen_before_raising()
    {
        var vm = await StartedAsync(Smt);
        vm.OpenPanelCommand.Execute(null);

        Assert.True(vm.MustChooseMachine);
        Assert.False(vm.RaiseCommand.CanExecute(vm.Types[0]));

        vm.SelectedMachine = vm.Machines.Single(m => m.Code == "L1-REFLOW-01");
        await vm.RaiseCommand.ExecuteAsync(vm.Types.Single(t => t.Code == AndonTypes.Quality));

        Assert.Equal([(2, "Quality")], _api.Raised);
        Assert.Equal(string.Format(Strings.Andon_Sent, Strings.Andon_Type_Quality, "L1-REFLOW-01"), vm.Message);
        Assert.False(vm.IsPanelOpen);
    }

    // The auto-raise (or a colleague) was first: no second call, the operator is told it is already open.
    [Fact]
    public async Task Already_open_call_is_reported_not_duplicated()
    {
        _api.RaiseAnswer = new RaiseAndonResult(Call(5, 2, "L1-REFLOW-01", "Machine", "Acknowledged", 120, ackBy: "mt01"), false);
        var vm = await StartedAsync(Smt);
        vm.SelectedMachine = vm.Machines.Single(m => m.Code == "L1-REFLOW-01");

        await vm.RaiseCommand.ExecuteAsync(vm.Types.Single(t => t.Code == AndonTypes.Machine));

        Assert.Equal(string.Format(Strings.Andon_AlreadyOpen, Strings.Andon_Type_Machine, "L1-REFLOW-01", "Acknowledged"), vm.Message);
        Assert.Contains("mt01", vm.SelectedMachine!.AndonText);
    }

    [Fact]
    public async Task Station_without_machines_has_no_andon()
    {
        var vm = await StartedAsync(Pack);

        Assert.False(vm.HasMachines);
        Assert.Equal(Strings.Andon_NoMachine, vm.Message);
        Assert.False(Rt.Started);
    }

    // The operator sees that help is coming: Raised -> Acknowledged by -> gone when resolved.
    [Fact]
    public async Task Banner_follows_the_andon_and_the_machine_state()
    {
        var vm = await StartedAsync(Smt);
        var reflow = vm.Machines.Single(m => m.Code == "L1-REFLOW-01");
        Assert.Equal(("Down", string.Format(Strings.Andon_Status, "Machine", "Raised")), (reflow.StateText, reflow.AndonText));

        Rt.Push(Call(5, 2, "L1-REFLOW-01", "Machine", "Acknowledged", 110, ackBy: "mt01"));
        Assert.Equal(string.Format(Strings.Andon_Status, "Machine", string.Format(Strings.Dashboard_AckBy, "mt01")), reflow.AndonText);

        Rt.Push(Call(5, 2, "L1-REFLOW-01", "Machine", "Resolved", 120));
        Rt.Push(new MachineStateChangedDto("L1-REFLOW-01", "L1", WireState.Running, T0.AddMinutes(9)));
        Assert.Equal(("Running", ""), (reflow.StateText, reflow.AndonText));
    }

    [Fact]
    public async Task Reconnect_banner_and_close()
    {
        var vm = await StartedAsync(Test);

        Rt.Drop();
        Assert.True(vm.IsReconnecting);
        await Rt.ReconnectAsync();
        Assert.False(vm.IsReconnecting);

        await vm.CloseAsync();
        Assert.True(Rt.Disposed);
    }

    // The scan screen owns the panel: opening it starts the panel, leaving it closes the connection.
    [Fact]
    public async Task Scan_screen_starts_and_closes_the_panel()
    {
        var k = new FakeKiosk();
        k.Context.SetStation(FakeExecution.Asm);
        k.Machines.MachinesByWorkCenter[FakeExecution.Asm.WorkCenterId!.Value] = [Dto(3, "L1-GLUE-01")];
        var scan = k.Scan();

        await scan.LoadAsync();
        await Task.Yield();
        await scan.CloseAsync();

        Assert.Equal("L1-GLUE-01", scan.Andon.SelectedMachine?.Code);
        Assert.True(k.Realtime.Client.Disposed);
    }
}
