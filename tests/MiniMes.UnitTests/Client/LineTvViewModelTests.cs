using System.Net;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using MiniMes.Client.Core;
using MiniMes.Client.Core.Auth;
using MiniMes.Client.Core.Http;
using MiniMes.Client.Core.Resources;
using MiniMes.Client.Core.ViewModels.Oee;
using MiniMes.Contracts.Auth;
using MiniMes.Contracts.Machines;
using MiniMes.Contracts.Oee;
using MiniMes.Contracts.Realtime;
using static MiniMes.UnitTests.Client.FakeMachineApi;

namespace MiniMes.UnitTests.Client;

// [v2] Spec M5 step 8b: the Line TV without a server: logs in by itself, rotates lines, shows Andon calls at once,
// says Offline when the API is gone, logs in again when the token expired.
public class LineTvViewModelTests
{
    private sealed class BadgeAuth : IAuthClient
    {
        public List<(string Badge, string Station)> Logins { get; } = [];
        public Exception? Fails { get; set; }

        public Task<LoginResponse> LoginAsync(string employeeCode, string password, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<LoginResponse> LoginOperatorAsync(string employeeCode, string stationCode, CancellationToken ct = default)
        {
            if (Fails is { } ex) return Task.FromException<LoginResponse>(ex);
            Logins.Add((employeeCode, stationCode));
            return Task.FromResult(new LoginResponse("t", T0.AddHours(8), employeeCode, "Line TV", ["Operator"], stationCode));
        }

        public void Logout() { }
    }

    private readonly FakeOeeApi _oee = new();
    private readonly FakeMachineApi _machines = new();
    private readonly BadgeAuth _auth = new();
    private readonly FakeRealtimeFactory _rt = new();
    private readonly FakeTimeProvider _time = new(T0);

    public LineTvViewModelTests()
    {
        _time.SetLocalTimeZone(TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh"));
        _machines.Status["L1"] = new LineStatusDto("L1", [Machine(1, "L1-SMT-01"), Machine(2, "L1-REFLOW-01", MachineState.Down)]);
        _machines.Status["L2"] = new LineStatusDto("L2", [Machine(9, "L2-SMT-01")]);
        _machines.Open["L1"] = [Call(5, 1, "L1-SMT-01", "Machine", "Raised", 100, level: 1)];
    }

    private LineTvViewModel Vm(string lines = "L1", int rotate = 20) => new(_oee, _machines, _auth, _rt, new ImmediateDispatcher(),
        Options.Create(new ClientOptions { ApiBaseUrl = "http://x", StationCode = "L1-TEST-01", Mode = "linetv", Line = lines,
            LineTvRotateSeconds = rotate }), _time);

    [Fact]
    public async Task Logs_in_with_its_badge_and_shows_the_running_shift()
    {
        var vm = Vm();

        await vm.LoadAsync();

        Assert.Equal([("LINE-TV", "L1-TEST-01")], _auth.Logins);
        Assert.Equal(["L1"], _rt.Client.Joined);
        Assert.Equal((null, null, null, "L1"), _oee.ShiftCalls[^1]);
        Assert.Equal(("80%", "970", true), (vm.OeeText, vm.GoodText, vm.IsBehind));   // 970 good, plan 405 min / 20 s = 1,215
        Assert.Equal(string.Format(Strings.LineTv_Plan, 1215), vm.PlanText);
        Assert.Equal([("06:00", 180L, 168L, "▼ -12"), ("08:00", 135L, 118L, "▼ -17"), ("11:00", 0L, 0L, "▲ +0")],
            vm.Hours.Select(h => (h.Hour, h.Plan, h.Actual, h.GapText)));
        Assert.Equal(1, vm.AndonCount);
        Assert.Equal(["L1-SMT-01", "L1-REFLOW-01"], vm.Tiles.Select(t => t.Code));
        Assert.False(vm.IsOffline);
    }

    // Spec 2.8: several lines on one TV, one after the other every 20 s; switched off with 0.
    [Fact]
    public async Task Several_lines_rotate_every_20_seconds()
    {
        var vm = Vm("L1, L2");
        await vm.LoadAsync();
        Assert.Equal(["L1", "L2"], _rt.Client.Joined);   // both groups once: no rejoin on every turn

        _time.Advance(TimeSpan.FromSeconds(20));
        await WaitUntilAsync(() => vm.CurrentLine == "L2" && _oee.ShiftCalls[^1].Line == "L2");
        Assert.Equal(["L2-SMT-01"], vm.Tiles.Select(t => t.Code));

        _time.Advance(TimeSpan.FromSeconds(20));
        await WaitUntilAsync(() => vm.CurrentLine == "L1");
    }

    // Spec step 8b check: "force an Andon -> shown at once" (a push, not the next 15 s refresh).
    [Fact]
    public async Task Andon_changes_show_at_once_for_the_line_on_screen()
    {
        var vm = await LoadedAsync();

        _rt.Client.Push(Call(6, 2, "L1-REFLOW-01", "Material", "Raised", 200));
        Assert.Equal(2, vm.AndonCount);
        _rt.Client.Push(Call(7, 9, "L2-SMT-01", "Machine", "Raised", 300, line: "L2"));   // not on screen
        Assert.Equal(2, vm.AndonCount);
        _rt.Client.Push(Call(5, 1, "L1-SMT-01", "Machine", "Resolved", 400));
        Assert.Equal([6L], vm.Andons.Select(a => a.Id));
        _rt.Client.Push(new MachineStateChangedDto("L1-SMT-01", "L1", MachineState.Down, T0));
        Assert.Equal(MachineState.Down, vm.Tiles[0].State);
    }

    // Spec 2.8: API lost -> "Offline - data at hh:mm", never old numbers that look new; back -> band gone.
    [Fact]
    public async Task Lost_api_shows_the_offline_band_with_the_time_of_the_last_data()
    {
        var vm = await LoadedAsync();
        _machines.Fails = new ApiException(null, "Cannot reach the server.");

        _time.Advance(LineTvViewModel.RefreshInterval);
        await WaitUntilAsync(() => vm.IsOffline);
        Assert.Equal(string.Format(Strings.LineTv_Offline, "09:00"), vm.OfflineText);   // T0 = 09:00 plant time
        Assert.Equal("80%", vm.OeeText);                                                  // still there, under the band

        _machines.Fails = null;
        _time.Advance(LineTvViewModel.RefreshInterval);
        await WaitUntilAsync(() => !vm.IsOffline);
    }

    [Fact]
    public async Task Lost_signalr_also_shows_the_band()
    {
        var vm = await LoadedAsync();

        _rt.Client.Drop();

        Assert.True(vm.IsOffline);
    }

    // A TV runs for weeks; the token does not. 401 -> log in again on the next tick.
    [Fact]
    public async Task Expired_token_logs_in_again()
    {
        var vm = await LoadedAsync();
        _machines.Fails = new ApiException(HttpStatusCode.Unauthorized, "Token expired.");
        _time.Advance(LineTvViewModel.RefreshInterval);
        await WaitUntilAsync(() => vm.IsOffline);

        _machines.Fails = null;
        _time.Advance(LineTvViewModel.RefreshInterval);
        await WaitUntilAsync(() => _auth.Logins.Count == 2 && !vm.IsOffline);
    }

    [Theory]
    [InlineData("linetv", "L1", true, new[] { "L1" })]
    [InlineData("LineTV", " L1 , L2 ,", true, new[] { "L1", "L2" })]
    [InlineData(null, null, false, new string[0])]
    public void Command_line_mode_and_lines(string? mode, string? line, bool isTv, string[] lines)
    {
        var o = new ClientOptions { Mode = mode, Line = line };

        Assert.Equal(isTv, o.IsLineTv);
        Assert.Equal(lines, o.LineTvLines);
    }

    private async Task<LineTvViewModel> LoadedAsync()
    {
        var vm = Vm();
        await vm.LoadAsync();
        return vm;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < until) await Task.Delay(20);
        Assert.True(condition());
    }
}
