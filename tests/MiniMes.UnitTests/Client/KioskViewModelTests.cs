using System.Net;
using MiniMes.Client.Core.Http;
using MiniMes.Client.Core.Resources;

namespace MiniMes.UnitTests.Client;

// Spec M3 step 6: kiosk after the badge login -> choose the WO (dispatch list) -> scan screen (READY).
public class KioskViewModelTests
{
    [Fact]
    public async Task Dispatch_list_of_the_station_line_is_shown_with_the_most_urgent_selected()
    {
        var kiosk = new FakeKiosk();
        var vm = kiosk.Select();

        await vm.LoadAsync();

        Assert.Equal(1, kiosk.Dispatch.AskedLine);   // the line of L1-ASM-01
        Assert.Equal(["WO-2610-00005", "WO-2610-00003"], vm.Rows.Select(r => r.Wo.WoNo));
        Assert.Equal("WO-2610-00005", vm.Selected?.Wo.WoNo);
        Assert.Equal(("12 / 200", "Pending"), (vm.Rows[0].Progress, vm.Rows[0].Fai));
        Assert.True(vm.StartCommand.CanExecute(null));
        Assert.Equal("Nguyen Van Nam (EMP00123)", vm.OperatorText);
        Assert.Equal(FakeExecution.Asm, kiosk.Context.Station);
    }

    [Fact]
    public async Task Start_opens_the_session_and_shows_the_ready_screen()
    {
        var kiosk = new FakeKiosk();
        var vm = kiosk.Select();
        await vm.LoadAsync();

        await vm.StartCommand.ExecuteAsync(null);
        var scan = kiosk.Scan();

        Assert.Equal([(2, 5)], kiosk.Fake.Opened);
        Assert.Equal("Scan", kiosk.Navigation.Last);
        Assert.Equal(77, kiosk.Context.Session?.Id);
        Assert.Equal(("L1-ASM-01", "WO-2610-00005 - PCB-A", Strings.Scan_Ready), (scan.StationCode, scan.WorkOrderText, scan.Headline));
    }

    [Fact]
    public async Task Refused_session_stays_on_the_list_with_the_server_message()
    {
        var kiosk = new FakeKiosk { Execution = new FakeExecution { OpenFails = new ApiException(HttpStatusCode.UnprocessableEntity, "Work order WO-2610-00005 is Completed; it is not on the line.") } };
        var vm = kiosk.Select();
        await vm.LoadAsync();

        await vm.StartCommand.ExecuteAsync(null);

        Assert.Equal("Work order WO-2610-00005 is Completed; it is not on the line.", vm.ErrorMessage);
        Assert.Equal(string.Empty, kiosk.Navigation.Last);
        Assert.Null(kiosk.Context.Session);
    }

    [Fact]
    public async Task Empty_dispatch_list_says_so_and_cannot_start()
    {
        var kiosk = new FakeKiosk { Dispatch = new FakeDispatch() };
        var vm = kiosk.Select();

        await vm.LoadAsync();

        Assert.Equal(Strings.StationSelect_NoWorkOrders, vm.StatusMessage);
        Assert.False(vm.StartCommand.CanExecute(null));
    }

    [Fact]
    public async Task Unknown_station_in_appsettings_is_reported()
    {
        var kiosk = new FakeKiosk();
        var vm = kiosk.Select("L9-NOPE");

        await vm.LoadAsync();

        Assert.Equal(string.Format(Strings.Kiosk_StationNotFound, "L9-NOPE"), vm.ErrorMessage);
        Assert.Empty(vm.Rows);
    }

    [Fact]
    public async Task Server_down_while_loading_is_shown_not_thrown()
    {
        var kiosk = new FakeKiosk { Execution = new FakeExecution { StationsFail = new ApiException(null, Strings.Error_CannotReachServer) } };
        var vm = kiosk.Select();

        await vm.LoadAsync();

        Assert.Equal(Strings.Error_CannotReachServer, vm.ErrorMessage);
    }

    // Logout closes the session on the server, and still logs out when the server cannot be reached.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Logout_closes_the_session_and_forgets_everything(bool serverDown)
    {
        var kiosk = new FakeKiosk { Execution = new FakeExecution { CloseFails = serverDown ? new ApiException(null, "down") : null } };
        var select = kiosk.Select();
        await select.LoadAsync();
        await select.StartCommand.ExecuteAsync(null);

        await kiosk.Scan().LogoutCommand.ExecuteAsync(null);

        Assert.Equal([(2, 77L)], kiosk.Fake.Closed);
        Assert.Equal("Login", kiosk.Navigation.Last);
        Assert.Null(kiosk.Tokens.Session);
        Assert.Null(kiosk.Context.Station);
        Assert.Null(kiosk.Context.Session);
    }

    [Fact]
    public void Change_wo_goes_back_to_the_list()
    {
        var kiosk = new FakeKiosk();

        kiosk.Scan().ChangeWorkOrderCommand.Execute(null);

        Assert.Equal("Home", kiosk.Navigation.Last);
    }
}
