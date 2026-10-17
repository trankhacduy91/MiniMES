using System.Net;
using MiniMes.Client.Core.Http;
using MiniMes.Client.Core.Kiosk;
using MiniMes.Client.Core.Resources;
using MiniMes.Client.Core.ViewModels.Kiosk;
using MiniMes.Contracts.Execution;
using MiniMes.Contracts.Planning;

namespace MiniMes.UnitTests.Client;

// Spec M3 step 8: NG at the test station (NG -> defect -> scan) and the Repair station (check-in -> form -> Done).
public class TestAndRepairViewModelTests
{
    private const string Board = "PCBA2610170000011";

    // The seeded PCB-A routing: only TEST (work center 3) is an inspection step.
    private static WorkOrderDetailDto Detail() => new(
        5, "WO-2610-00005", 1, "PCB-A", "Controller board A", 1, "L1", 200,
        new DateTime(2026, 11, 2, 1, 0, 0, DateTimeKind.Utc), new DateTime(2026, 11, 2, 5, 0, 0, DateTimeKind.Utc),
        "InProgress", 1, false, "NotRequired", null, 1, 1, null, null, null, null, null, null, null, 0, 200, [], "AAAAAAAAB9E=", [],
        [new WorkOrderOperationDto(10, "SMT", 1, "WC-SMT", 30m, false, true, null, 0, 0),
         new WorkOrderOperationDto(20, "ASSY", 2, "WC-ASSY", 60m, false, true, null, 0, 0),
         new WorkOrderOperationDto(30, "TEST", 3, "WC-TEST", 45m, true, true, null, 0, 0),
         new WorkOrderOperationDto(40, "PACK", 4, "WC-PACK", 20m, false, true, null, 0, 0)]);

    private static async Task<(FakeKiosk Kiosk, ScanViewModel Vm)> TestStationAsync()
    {
        var kiosk = new FakeKiosk().Started(FakeKiosk.Test);
        kiosk.Dispatch.Detail = Detail();
        kiosk.Fake.Scan = (r, _) => Task.FromResult(r.Result == "NG"
            ? new ScanResponse("Ng", "NG recorded", null, null, null, false)
            : new ScanResponse("Ok", "OK", null, 40, "PACK", false));
        var vm = kiosk.Scan();
        await vm.LoadAsync();
        return (kiosk, vm);
    }

    private static Task ScanAsync(ScanViewModel vm, string serial = Board)
    {
        vm.ScanInput = serial;
        return vm.ScanCommand.ExecuteAsync(null);
    }

    // ---------- test station ----------

    [Fact]
    public async Task Inspection_step_offers_ng_with_the_active_defect_codes()
    {
        var (_, vm) = await TestStationAsync();

        Assert.True(vm.CanReportNg);
        Assert.Equal(["SLD-BRG", "CMP-MIS"], vm.DefectCodes.Select(d => d.Code));
    }

    [Fact]
    public async Task Other_steps_have_no_ng_button()
    {
        var kiosk = new FakeKiosk().Started();   // L1-ASM-01: ASSY is not an inspection
        kiosk.Dispatch.Detail = Detail();
        var vm = kiosk.Scan();

        await vm.LoadAsync();

        Assert.False(vm.CanReportNg);
        Assert.Equal(0, kiosk.Fake.DefectCodeLoads);
    }

    [Fact]
    public async Task Ng_then_defect_then_scan_records_ng_once()
    {
        var (kiosk, vm) = await TestStationAsync();

        vm.OpenNgPanelCommand.Execute(null);
        Assert.True(vm.IsNgPanelOpen);
        vm.ChooseDefectCommand.Execute(FakeExecution.Bridge);
        Assert.Equal((false, true), (vm.IsNgPanelOpen, vm.IsNgArmed));
        Assert.Equal(string.Format(Strings.Scan_NgArmed, "SLD-BRG", "Solder bridge"), vm.ArmedText);
        await ScanAsync(vm);

        Assert.Equal(("NG", "SLD-BRG"), (kiosk.Fake.Scans[0].Result, kiosk.Fake.Scans[0].DefectCode));
        Assert.Equal((ScanState.Ng, Strings.Scan_Ng, string.Format(Strings.Scan_NgRecorded, "SLD-BRG")), (vm.State, vm.Headline, vm.Message));
        Assert.Equal([ScanState.Ng], kiosk.Feedback.Played);
        Assert.False(vm.IsNgArmed);

        await ScanAsync(vm, "PCBA2610170000029");   // the next board is OK again
        Assert.Equal(("OK", (string?)null), (kiosk.Fake.Scans[1].Result, kiosk.Fake.Scans[1].DefectCode));
    }

    [Fact]
    public async Task Cancelled_ng_scans_ok()
    {
        var (kiosk, vm) = await TestStationAsync();
        vm.ChooseDefectCommand.Execute(FakeExecution.Missing);

        vm.CancelNgCommand.Execute(null);
        await ScanAsync(vm);

        Assert.Equal("OK", kiosk.Fake.Scans[0].Result);
        Assert.False(vm.IsNgArmed);
    }

    // A refused NG (board already NG, wrong step...) does not leave NG armed for the next board.
    [Fact]
    public async Task Blocked_ng_scan_disarms_too()
    {
        var (kiosk, vm) = await TestStationAsync();
        kiosk.Fake.Scan = (_, _) => Task.FromResult(new ScanResponse("Blocked", "Serial is NG: send it to Repair.", "MustGoRepair", null, null, false));
        vm.ChooseDefectCommand.Execute(FakeExecution.Bridge);

        await ScanAsync(vm);

        Assert.Equal((ScanState.Blocked, Strings.Block_MustGoRepair), (vm.State, vm.Message));
        Assert.False(vm.IsNgArmed);
    }

    // ---------- repair station ----------

    private static async Task<(FakeKiosk Kiosk, RepairViewModel Vm)> RepairStationAsync()
    {
        var kiosk = new FakeKiosk().Started(FakeKiosk.RepairStation);
        kiosk.Dispatch.Detail = Detail();
        var vm = kiosk.Repair();
        await vm.LoadAsync();
        return (kiosk, vm);
    }

    private static async Task CheckInAsync(RepairViewModel vm, string serial = Board)
    {
        vm.ScanInput = serial;
        await vm.ScanCommand.ExecuteAsync(null);
    }

    [Fact]
    public async Task Check_in_opens_the_form_and_stops_scanning()
    {
        var (kiosk, vm) = await RepairStationAsync();

        await CheckInAsync(vm, " pcba2610170000011 ");

        Assert.Equal(("OK", Board), (kiosk.Fake.Scans[0].Result, kiosk.Fake.Scans[0].SerialNo));
        Assert.Equal((true, Board), (vm.IsFormOpen, vm.Board));
        Assert.Equal((ScanState.Ok, Strings.Repair_CheckedIn), (vm.State, vm.Headline));
        Assert.False(vm.ScanCommand.CanExecute(null));
        Assert.False(vm.SubmitCommand.CanExecute(null));   // defect and action still empty
        Assert.Equal([null, 10, 20, 30, 40], vm.ReturnOptions.Select(o => o.Seq));
        Assert.Equal(Strings.Repair_RetestFailedStep, vm.SelectedReturn?.Text);
    }

    [Fact]
    public async Task Good_board_is_refused_and_no_form_opens()
    {
        var (kiosk, vm) = await RepairStationAsync();
        kiosk.Fake.Scan = (_, _) => Task.FromResult(new ScanResponse("Blocked", "Serial is not defective.", "NotDefective", null, null, false));

        await CheckInAsync(vm);

        Assert.False(vm.IsFormOpen);
        Assert.Equal((ScanState.Blocked, Strings.Block_NotDefective), (vm.State, vm.Message));
    }

    [Fact]
    public async Task Done_sends_the_repair_and_returns_to_scanning()
    {
        var (kiosk, vm) = await RepairStationAsync();
        await CheckInAsync(vm);

        vm.SelectedDefect = FakeExecution.Bridge;
        vm.Action = "  Reflow solder joint U3 ";
        Assert.True(vm.SubmitCommand.CanExecute(null));
        await vm.SubmitCommand.ExecuteAsync(null);

        Assert.Equal(new RepairRequest(Board, "SLD-BRG", "Reflow solder joint U3", null, null), Assert.Single(kiosk.Fake.Repairs));
        Assert.Equal((false, ScanState.Ok, Strings.Repair_Done), (vm.IsFormOpen, vm.State, vm.Headline));
        Assert.Equal(string.Format(Strings.Scan_Next, "30 TEST"), vm.Message);
        Assert.True(vm.ScanCommand.CanExecute(null));
    }

    [Fact]
    public async Task Rework_sends_the_chosen_step_and_the_replaced_part()
    {
        var (kiosk, vm) = await RepairStationAsync();
        await CheckInAsync(vm);

        vm.SelectedDefect = FakeExecution.Missing;
        vm.Action = "Replace U3";
        vm.ComponentReplaced = "U3 IC-MCU-QFN lot L2610-07";
        vm.SelectedReturn = vm.ReturnOptions.Single(o => o.Seq == 10);
        await vm.SubmitCommand.ExecuteAsync(null);

        Assert.Equal(new RepairRequest(Board, "CMP-MIS", "Replace U3", "U3 IC-MCU-QFN lot L2610-07", 10), Assert.Single(kiosk.Fake.Repairs));
        Assert.Equal(string.Format(Strings.Scan_Next, "10 SMT"), vm.Message);
    }

    // Server says no (400 replaced part missing, 422 rework forward): the form stays filled with the reason.
    [Fact]
    public async Task Refused_repair_keeps_the_form_with_the_server_message()
    {
        var (kiosk, vm) = await RepairStationAsync();
        kiosk.Fake.Repair = _ => Task.FromException<RepairResponse>(new ApiException(HttpStatusCode.BadRequest, "A replacement needs the component replaced (reference and lot)."));
        await CheckInAsync(vm);
        vm.SelectedDefect = FakeExecution.Missing;
        vm.Action = "Replace U3";

        await vm.SubmitCommand.ExecuteAsync(null);

        Assert.True(vm.IsFormOpen);
        Assert.Equal(("A replacement needs the component replaced (reference and lot).", "Replace U3"), (vm.FormError, vm.Action));
    }

    [Fact]
    public async Task Server_down_on_done_keeps_the_form_and_goes_offline()
    {
        var (kiosk, vm) = await RepairStationAsync();
        kiosk.Fake.Repair = _ => Task.FromException<RepairResponse>(new ApiException(null, Strings.Error_CannotReachServer));
        await CheckInAsync(vm);
        vm.SelectedDefect = FakeExecution.Bridge;
        vm.Action = "Reflow";

        await vm.SubmitCommand.ExecuteAsync(null);

        Assert.True(vm.IsOffline);
        Assert.True(vm.IsFormOpen);
        Assert.False(vm.SubmitCommand.CanExecute(null));
        kiosk.Fake.Online = true;
        await vm.CheckConnectionCommand.ExecuteAsync(null);
        Assert.True(vm.SubmitCommand.CanExecute(null));   // online again: the same form can be sent
    }

    [Fact]
    public async Task Cancel_closes_the_form_and_the_next_board_can_be_scanned()
    {
        var (_, vm) = await RepairStationAsync();
        await CheckInAsync(vm);

        vm.CancelRepairCommand.Execute(null);

        Assert.False(vm.IsFormOpen);
        Assert.True(vm.ScanCommand.CanExecute(null));
    }
}
