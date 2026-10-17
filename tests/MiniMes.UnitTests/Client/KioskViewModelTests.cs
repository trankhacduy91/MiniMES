using System.Net;
using Microsoft.Extensions.Options;
using MiniMes.Client.Core;
using MiniMes.Client.Core.Api;
using MiniMes.Client.Core.Auth;
using MiniMes.Client.Core.Http;
using MiniMes.Client.Core.Kiosk;
using MiniMes.Client.Core.Navigation;
using MiniMes.Client.Core.Resources;
using MiniMes.Client.Core.ViewModels.Kiosk;
using MiniMes.Contracts.Auth;
using MiniMes.Contracts.Execution;
using MiniMes.Contracts.Factory;
using MiniMes.Contracts.Planning;

namespace MiniMes.UnitTests.Client;

// Spec M3 step 6: kiosk after the badge login -> choose the WO (dispatch list) -> scan screen (READY).
public class KioskViewModelTests
{
    private static readonly StationDto Asm = new(2, LineId: 1, "L1-ASM-01", "Assembly", "Assembly", true, 2);

    private static DispatchListItemDto Wo(int id, string woNo, int priority, string? fai = "Pending")
        => new(id, woNo, "PCB-A", 200, 12, priority, new DateTime(2026, 10, 19, 1, 0, 0, DateTimeKind.Utc), "Released", fai);

    private sealed class FakeExecution : IExecutionApi
    {
        public IReadOnlyList<StationDto> Stations { get; init; } = [Asm];
        public Exception? OpenFails { get; init; }
        public Exception? CloseFails { get; init; }
        public List<(int StationId, int WoId)> Opened { get; } = [];
        public List<(int StationId, long SessionId)> Closed { get; } = [];

        public Task<IReadOnlyList<StationDto>> GetStationsAsync(CancellationToken ct = default) => Task.FromResult(Stations);

        public Task<StationSessionDto> OpenSessionAsync(int stationId, int workOrderId, CancellationToken ct = default)
        {
            if (OpenFails is not null) return Task.FromException<StationSessionDto>(OpenFails);
            Opened.Add((stationId, workOrderId));
            return Task.FromResult(new StationSessionDto(77, stationId, "L1-ASM-01", workOrderId, "WO", "EMP00123", DateTime.UtcNow, null));
        }

        public Task CloseSessionAsync(int stationId, long sessionId, CancellationToken ct = default)
        {
            Closed.Add((stationId, sessionId));
            return CloseFails is null ? Task.CompletedTask : Task.FromException(CloseFails);
        }
    }

    private sealed class FakeDispatch(params DispatchListItemDto[] list) : FakeWorkOrderApi
    {
        public int? AskedLine { get; private set; }

        public override Task<IReadOnlyList<DispatchListItemDto>> GetDispatchListAsync(int lineId, CancellationToken ct = default)
        {
            AskedLine = lineId;
            return Task.FromResult<IReadOnlyList<DispatchListItemDto>>(list);
        }
    }

    private sealed class FakeNavigation : IKioskNavigation
    {
        public string Last { get; private set; } = string.Empty;
        public void ShowLogin() => Last = "Login";
        public void ShowHome() => Last = "Home";
        public void ShowScan() => Last = "Scan";
    }

    private sealed class FakeAuth(ITokenStore tokens) : IAuthClient
    {
        public Task<LoginResponse> LoginAsync(string employeeCode, string password, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<LoginResponse> LoginOperatorAsync(string employeeCode, string stationCode, CancellationToken ct = default) => throw new NotImplementedException();
        public void Logout() => tokens.Clear();
    }

    private sealed class Kiosk
    {
        public IExecutionApi Execution { get; init; } = new FakeExecution();
        public FakeDispatch Dispatch { get; init; } = new(Wo(5, "WO-2610-00005", 1), Wo(3, "WO-2610-00003", 5));
        public KioskContext Context { get; } = new();
        public FakeNavigation Navigation { get; } = new();
        public TokenStore Tokens { get; } = new();

        public Kiosk() => Tokens.Set(new LoginResponse("t", DateTime.UtcNow, "EMP00123", "Nguyen Van Nam", ["Operator"], "L1-ASM-01"));

        public StationSelectViewModel Select(string station = "L1-ASM-01")
            => new(Execution, Dispatch, Context, Navigation, new FakeAuth(Tokens), Tokens,
                Options.Create(new ClientOptions { ApiBaseUrl = "http://x/", StationCode = station }));

        public ScanViewModel Scan() => new(Execution, Context, Navigation, new FakeAuth(Tokens), Tokens);
    }

    [Fact]
    public async Task Dispatch_list_of_the_station_line_is_shown_with_the_most_urgent_selected()
    {
        var kiosk = new Kiosk();
        var vm = kiosk.Select();

        await vm.LoadAsync();

        Assert.Equal(1, kiosk.Dispatch.AskedLine);   // the line of L1-ASM-01
        Assert.Equal(["WO-2610-00005", "WO-2610-00003"], vm.Rows.Select(r => r.Wo.WoNo));
        Assert.Equal("WO-2610-00005", vm.Selected?.Wo.WoNo);
        Assert.Equal(("12 / 200", "Pending"), (vm.Rows[0].Progress, vm.Rows[0].Fai));
        Assert.True(vm.StartCommand.CanExecute(null));
        Assert.Equal("Nguyen Van Nam (EMP00123)", vm.OperatorText);
        Assert.Equal(Asm, kiosk.Context.Station);
    }

    [Fact]
    public async Task Start_opens_the_session_and_shows_the_ready_screen()
    {
        var kiosk = new Kiosk();
        var vm = kiosk.Select();
        await vm.LoadAsync();

        await vm.StartCommand.ExecuteAsync(null);
        var scan = kiosk.Scan();

        Assert.Equal([(2, 5)], ((FakeExecution)kiosk.Execution).Opened);
        Assert.Equal("Scan", kiosk.Navigation.Last);
        Assert.Equal(77, kiosk.Context.Session?.Id);
        Assert.Equal(("L1-ASM-01", "WO-2610-00005 - PCB-A", Strings.Scan_Ready), (scan.StationCode, scan.WorkOrderText, scan.StatusText));
    }

    [Fact]
    public async Task Refused_session_stays_on_the_list_with_the_server_message()
    {
        var kiosk = new Kiosk { Execution = new FakeExecution { OpenFails = new ApiException(HttpStatusCode.UnprocessableEntity, "Work order WO-2610-00005 is Completed; it is not on the line.") } };
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
        var kiosk = new Kiosk { Dispatch = new FakeDispatch() };
        var vm = kiosk.Select();

        await vm.LoadAsync();

        Assert.Equal(Strings.StationSelect_NoWorkOrders, vm.StatusMessage);
        Assert.False(vm.StartCommand.CanExecute(null));
    }

    [Fact]
    public async Task Unknown_station_in_appsettings_is_reported()
    {
        var kiosk = new Kiosk();
        var vm = kiosk.Select("L9-NOPE");

        await vm.LoadAsync();

        Assert.Equal(string.Format(Strings.Kiosk_StationNotFound, "L9-NOPE"), vm.ErrorMessage);
        Assert.Empty(vm.Rows);
    }

    [Fact]
    public async Task Server_down_while_loading_is_shown_not_thrown()
    {
        var kiosk = new Kiosk { Execution = new ThrowingStations() };
        var vm = kiosk.Select();

        await vm.LoadAsync();

        Assert.Equal(Strings.Error_CannotReachServer, vm.ErrorMessage);
    }

    private sealed class ThrowingStations : IExecutionApi
    {
        public Task<IReadOnlyList<StationDto>> GetStationsAsync(CancellationToken ct = default)
            => Task.FromException<IReadOnlyList<StationDto>>(new ApiException(null, Strings.Error_CannotReachServer));
        public Task<StationSessionDto> OpenSessionAsync(int stationId, int workOrderId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task CloseSessionAsync(int stationId, long sessionId, CancellationToken ct = default) => throw new NotImplementedException();
    }

    // Logout closes the session on the server, and still logs out when the server cannot be reached.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Logout_closes_the_session_and_forgets_everything(bool serverDown)
    {
        var kiosk = new Kiosk { Execution = new FakeExecution { CloseFails = serverDown ? new ApiException(null, "down") : null } };
        var select = kiosk.Select();
        await select.LoadAsync();
        await select.StartCommand.ExecuteAsync(null);

        await kiosk.Scan().LogoutCommand.ExecuteAsync(null);

        Assert.Equal([(2, 77L)], ((FakeExecution)kiosk.Execution).Closed);
        Assert.Equal("Login", kiosk.Navigation.Last);
        Assert.Null(kiosk.Tokens.Session);
        Assert.Null(kiosk.Context.Station);
        Assert.Null(kiosk.Context.Session);
    }

    [Fact]
    public void Change_wo_goes_back_to_the_list()
    {
        var kiosk = new Kiosk();

        kiosk.Scan().ChangeWorkOrderCommand.Execute(null);

        Assert.Equal("Home", kiosk.Navigation.Last);
    }
}
