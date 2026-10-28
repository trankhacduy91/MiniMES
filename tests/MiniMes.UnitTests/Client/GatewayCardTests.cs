using Microsoft.Extensions.Time.Testing;
using MiniMes.Client.Core.Auth;
using MiniMes.Client.Core.Resources;
using MiniMes.Client.Core.ViewModels.Machines;
using MiniMes.Contracts.Auth;
using MiniMes.Contracts.Machines;
using MiniMes.Contracts.Realtime;
using static MiniMes.UnitTests.Client.FakeMachineApi;

namespace MiniMes.UnitTests.Client;

// Spec M4b step 6: the gateway card on the line dashboard. Snapshot first, pushes on top, an older push never wins;
// a gateway that reads no machine of the line is not shown.
public class GatewayCardTests
{
    private readonly FakeMachineApi _api = new();
    private readonly FakeRealtimeFactory _rt = new();
    private readonly TokenStore _tokens = new();

    public GatewayCardTests()
    {
        _api.Status["L1"] = new LineStatusDto("L1", [Machine(1, "L1-SMT-01"), Machine(2, "L1-REFLOW-01", source: "OpcUa"), Machine(3, "L1-GLUE-01", source: "Modbus")],
            [Gw("GW01", "online", T0, ("L1-REFLOW-01", "connected"), ("L1-GLUE-01", "connected"))]);
        _tokens.Set(new LoginResponse("t", T0, "sup01", "Supervisor One", ["Supervisor"], null));
    }

    private async Task<LineDashboardViewModel> LoadedAsync()
    {
        var vm = new LineDashboardViewModel(_api, _rt, new ImmediateDispatcher(), _tokens, new FakeTimeProvider(T0));
        await vm.LoadAsync();
        return vm;
    }

    [Fact]
    public async Task Snapshot_shows_the_gateway_of_the_line()
    {
        var card = Assert.Single((await LoadedAsync()).Gateways);

        Assert.Equal(string.Format(Strings.Dashboard_Gateway, "GW01"), card.Title);
        Assert.True(card.IsOnline);
        Assert.Equal(Strings.Dashboard_GatewayOnline, card.StateText);
        Assert.Equal(string.Format(Strings.Dashboard_GatewayDevices, 2, 2), card.DevicesText);
        Assert.Equal(string.Format(Strings.Dashboard_GatewayQueue, 0), card.QueueText);
        Assert.Empty(card.DroppedText);
    }

    // OPC UA server stopped: the gateway is fine, one machine is lost.
    [Fact]
    public async Task A_lost_plc_shows_on_the_card_while_the_gateway_stays_online()
    {
        var vm = await LoadedAsync();

        _rt.Client.Push(Gw("GW01", "online", T0.AddSeconds(5), ("L1-REFLOW-01", "disconnected"), ("L1-GLUE-01", "connected")));

        var card = vm.Gateways.Single();
        Assert.True(card.IsOnline);
        Assert.Equal(string.Format(Strings.Dashboard_GatewayDevices, 1, 2), card.DevicesText);
    }

    // Gateway killed: the broker's LWT.
    [Fact]
    public async Task Lwt_turns_the_card_offline()
    {
        var vm = await LoadedAsync();

        _rt.Client.Push(Gw("GW01", "offline", T0.AddSeconds(5), ("L1-REFLOW-01", "disconnected"), ("L1-GLUE-01", "disconnected")));

        Assert.False(vm.Gateways.Single().IsOnline);
        Assert.Equal(Strings.Dashboard_GatewayOffline, vm.Gateways.Single().StateText);
    }

    [Fact]
    public async Task Queue_and_dropped_messages_are_shown()
    {
        var vm = await LoadedAsync();

        _rt.Client.Push(Gw("GW01", "online", T0.AddSeconds(5), ("L1-GLUE-01", "connected")) with { QueueLength = 61, OldestAgeSec = 83.6, DroppedCount = 50 });

        var card = vm.Gateways.Single();
        Assert.Equal(string.Format(Strings.Dashboard_GatewayQueueOldest, 61, 83.6), card.QueueText);
        Assert.Equal(string.Format(Strings.Dashboard_GatewayDropped, 50), card.DroppedText);
    }

    [Fact]
    public async Task An_older_status_does_not_roll_the_card_back()
    {
        var vm = await LoadedAsync();
        _rt.Client.Push(Gw("GW01", "offline", T0.AddSeconds(10)));

        _rt.Client.Push(Gw("GW01", "online", T0.AddSeconds(5), ("L1-GLUE-01", "connected")));

        Assert.False(vm.Gateways.Single().IsOnline);
    }

    [Fact]
    public async Task Gateway_of_another_line_is_not_shown_but_a_new_one_of_this_line_is()
    {
        var vm = await LoadedAsync();

        _rt.Client.Push(Gw("GW02", "online", T0, ("L2-SMT-01", "connected")));
        _rt.Client.Push(Gw("GW03", "online", T0, ("L1-SMT-01", "connected")));

        Assert.Equal(["GW01", "GW03"], vm.Gateways.Select(g => g.Gateway));
    }

    private static GatewayStatusDto Gw(string id, string state, DateTime at, params (string Machine, string State)[] devices)
        => new(id, "VN-BN01", state, 0, 0, 0, devices.ToDictionary(d => d.Machine, d => d.State), at);
}
