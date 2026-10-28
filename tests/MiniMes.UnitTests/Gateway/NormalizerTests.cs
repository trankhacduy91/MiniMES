using Microsoft.Extensions.Logging.Abstractions;
using MiniMes.Contracts.Machines;
using MiniMes.EdgeGateway.Config;
using MiniMes.EdgeGateway.Drivers;
using MiniMes.EdgeGateway.Drivers.Modbus;
using MiniMes.EdgeGateway.Pipeline;

namespace MiniMes.UnitTests.Gateway;

// Spec M4b 2.6 table: what goes out, on which topic, and when (state/alarm on change, count on change + 30 s,
// process beyond the deadband + 60 s), plus the quality rules.
public class NormalizerTests
{
    private static readonly DateTime T0 = new(2026, 10, 28, 8, 0, 0, DateTimeKind.Utc);
    private const string Uns = "minimes/VN-BN01/SMT-ASSY/L1";

    private readonly Normalizer _n = new(Options(), NullLogger<Normalizer>.Instance);

    // ---------- count ----------

    [Fact]
    public void Count_goes_to_the_count_topic_with_the_counter()
    {
        var m = Single(_n.Accept(Raw("L1-GLUE-01", "counter", 100_000u), T0));

        Assert.Equal($"{Uns}/L1-GLUE-01/count", m.Topic);
        Assert.Equal(100_000, m.Message.Counter);
        Assert.Equal("L1-GLUE-01", m.Message.Machine);
        Assert.Equal("modbus", m.Message.Source);
        Assert.False(m.Retain);
    }

    [Fact]
    public void Count_is_not_repeated_at_every_poll_but_again_after_30_s()
    {
        _n.Accept(Raw("L1-GLUE-01", "counter", 100u), T0);

        Assert.Empty(_n.Accept(Raw("L1-GLUE-01", "counter", 100u, T0.AddSeconds(1)), T0.AddSeconds(1)));
        Assert.Empty(_n.Accept(Raw("L1-GLUE-01", "counter", 100u, T0.AddSeconds(29)), T0.AddSeconds(29)));
        Assert.Single(_n.Accept(Raw("L1-GLUE-01", "counter", 100u, T0.AddSeconds(30)), T0.AddSeconds(30)));
    }

    [Fact]
    public void Count_goes_out_on_every_change()
    {
        _n.Accept(Raw("L1-GLUE-01", "counter", 100u), T0);

        var m = Single(_n.Accept(Raw("L1-GLUE-01", "counter", 101u, T0.AddSeconds(1)), T0.AddSeconds(1)));

        Assert.Equal(101, m.Message.Counter);
    }

    // OPC UA sends nothing while the counter stays: the 30 s message comes from the tick, stamped "now".
    [Fact]
    public void Refresh_repeats_an_unchanged_counter_after_30_s()
    {
        _n.Accept(Raw("L1-REFLOW-01", "counter", 8000u), T0);

        Assert.Empty(_n.Refresh(T0.AddSeconds(29)));
        var m = Single(_n.Refresh(T0.AddSeconds(30)));

        Assert.Equal(8000, m.Message.Counter);
        Assert.Equal(T0.AddSeconds(30), m.Message.Ts.UtcDateTime);
        Assert.Equal("opcua", m.Message.Source);
    }

    // The 30 s rule runs on the gateway's clock: a PLC two hours behind must not trigger a refresh at every tick.
    [Fact]
    public void A_skewed_plc_clock_does_not_change_the_refresh_rhythm()
    {
        _n.Accept(Raw("L1-REFLOW-01", "counter", 8000u, T0.AddHours(-2)), T0);

        Assert.Empty(_n.Refresh(T0.AddSeconds(5)));
        Assert.Single(_n.Refresh(T0.AddSeconds(30)));
    }

    [Fact]
    public void Bad_count_is_not_sent()
    {
        Assert.Empty(_n.Accept(Raw("L1-GLUE-01", "counter", null, quality: DataQuality.Bad), T0));
    }

    [Fact]
    public void Negative_counter_is_dropped()
    {
        Assert.Empty(_n.Accept(Raw("L1-GLUE-01", "counter", -5), T0));
    }

    // ---------- state ----------

    [Fact]
    public void State_is_mapped_and_retained()
    {
        var m = Single(_n.Accept(Raw("L1-GLUE-01", "state", (ushort)3), T0));

        Assert.Equal($"{Uns}/L1-GLUE-01/state", m.Topic);
        Assert.Equal(MachineState.Down, m.Message.State);
        Assert.True(m.Retain);
    }

    [Fact]
    public void State_goes_out_only_when_it_changes()
    {
        _n.Accept(Raw("L1-GLUE-01", "state", (ushort)1), T0);

        Assert.Empty(_n.Accept(Raw("L1-GLUE-01", "state", (ushort)1, T0.AddSeconds(1)), T0.AddSeconds(1)));
        Assert.Empty(_n.Refresh(T0.AddMinutes(10)));   // no periodic state: retained + heartbeat
        Assert.Single(_n.Accept(Raw("L1-GLUE-01", "state", (ushort)2, T0.AddSeconds(2)), T0.AddSeconds(2)));
    }

    [Fact]
    public void Unknown_state_code_is_dropped()
    {
        Assert.Empty(_n.Accept(Raw("L1-GLUE-01", "state", (ushort)9), T0));
        Assert.Null(_n.CurrentState("L1-GLUE-01"));
    }

    [Fact]
    public void Opc_ua_int32_state_is_mapped_too()
    {
        var m = Single(_n.Accept(Raw("L1-REFLOW-01", "state", 1), T0));

        Assert.Equal(MachineState.Running, m.Message.State);
        Assert.Equal(MachineState.Running, _n.CurrentState("L1-REFLOW-01"));
    }

    // ---------- alarm ----------

    [Fact]
    public void Alarm_zero_at_start_is_not_news()
    {
        Assert.Empty(_n.Accept(Raw("L1-GLUE-01", "alarm", (ushort)0), T0));
    }

    [Fact]
    public void Alarm_raised_then_cleared()
    {
        _n.Accept(Raw("L1-GLUE-01", "alarm", (ushort)0), T0);

        var raised = Single(_n.Accept(Raw("L1-GLUE-01", "alarm", (ushort)301, T0.AddSeconds(1)), T0.AddSeconds(1)));
        Assert.Empty(_n.Accept(Raw("L1-GLUE-01", "alarm", (ushort)301, T0.AddSeconds(2)), T0.AddSeconds(2)));
        var cleared = Single(_n.Accept(Raw("L1-GLUE-01", "alarm", (ushort)0, T0.AddSeconds(3)), T0.AddSeconds(3)));

        Assert.Equal($"{Uns}/L1-GLUE-01/alarm", raised.Topic);
        Assert.Equal("301", raised.Message.AlarmCode);
        Assert.Null(cleared.Message.AlarmCode);   // M4: null alarmCode = alarm cleared
    }

    // ---------- process ----------

    [Fact]
    public void Process_goes_to_its_tag_topic()
    {
        var m = Single(_n.Accept(Raw("L1-REFLOW-01", "peakTemp", 245.3), T0));

        Assert.Equal($"{Uns}/L1-REFLOW-01/process/peakTemp", m.Topic);
        Assert.Equal("peakTemp", m.Message.Tag);
        Assert.Equal(245.3, m.Message.Value);
        Assert.Equal(DataQuality.Good, m.Message.Quality);
    }

    [Theory]
    [InlineData(245.5, false)]   // 0.5 = the deadband: not beyond it
    [InlineData(245.51, true)]
    [InlineData(244.49, true)]
    [InlineData(245.2, false)]
    public void Process_goes_out_beyond_the_deadband(double next, bool sent)
    {
        _n.Accept(Raw("L1-REFLOW-01", "peakTemp", 245.0), T0);

        var out1 = _n.Accept(Raw("L1-REFLOW-01", "peakTemp", next, T0.AddSeconds(1)), T0.AddSeconds(1));

        Assert.Equal(sent ? 1 : 0, out1.Count);
    }

    [Fact]
    public void Process_is_repeated_after_60_s_even_without_change()
    {
        _n.Accept(Raw("L1-REFLOW-01", "peakTemp", 245.0), T0);

        Assert.Empty(_n.Refresh(T0.AddSeconds(59)));
        Assert.Single(_n.Refresh(T0.AddSeconds(60)));
    }

    // Rule G8 within the M4 contract: a process message must carry a value, so a broken sensor is reported once with
    // the last good value and quality Bad; nothing more until it is Good again.
    [Fact]
    public void Bad_sensor_is_sent_once_with_the_last_good_value()
    {
        _n.Accept(Raw("L1-REFLOW-01", "peakTemp", 245.4), T0);

        var bad = Single(_n.Accept(Raw("L1-REFLOW-01", "peakTemp", null, T0.AddSeconds(1), DataQuality.Bad), T0.AddSeconds(1)));
        Assert.Empty(_n.Accept(Raw("L1-REFLOW-01", "peakTemp", null, T0.AddSeconds(2), DataQuality.Bad), T0.AddSeconds(2)));
        var good = Single(_n.Accept(Raw("L1-REFLOW-01", "peakTemp", 245.5, T0.AddSeconds(3)), T0.AddSeconds(3)));

        Assert.Equal((245.4, DataQuality.Bad), (bad.Message.Value, bad.Message.Quality));
        Assert.Equal(DataQuality.Good, good.Message.Quality);   // back to Good: sent although within the deadband
    }

    [Fact]
    public void Bad_sensor_before_any_good_value_sends_nothing()
    {
        Assert.Empty(_n.Accept(Raw("L1-REFLOW-01", "peakTemp", null, quality: DataQuality.Bad), T0));
    }

    [Fact]
    public void Float_register_keeps_its_decimals()
    {
        var m = Single(_n.Accept(Raw("L1-GLUE-01", "glueTemp", 45.1f), T0));

        Assert.Equal(45.1, m.Message.Value);   // not 45.099998474121094
    }

    // ---------- msgId, disconnect ----------

    [Fact]
    public void Same_reading_gives_the_same_msg_id()
    {
        var a = Single(new Normalizer(Options(), NullLogger<Normalizer>.Instance).Accept(Raw("L1-REFLOW-01", "counter", 8001u), T0));
        var b = Single(new Normalizer(Options(), NullLogger<Normalizer>.Instance).Accept(Raw("L1-REFLOW-01", "counter", 8001u), T0));

        Assert.Equal(a.Message.MsgId, b.Message.MsgId);
    }

    [Fact]
    public void After_a_disconnect_everything_goes_out_again_and_nothing_is_refreshed()
    {
        _n.Accept(Raw("L1-GLUE-01", "state", (ushort)1), T0);
        _n.Accept(Raw("L1-GLUE-01", "counter", 100u), T0);

        _n.Disconnected("L1-GLUE-01");

        Assert.Empty(_n.Refresh(T0.AddMinutes(5)));   // no "still 100" for a machine we cannot read
        Assert.Null(_n.CurrentState("L1-GLUE-01"));   // no heartbeat
        Assert.Single(_n.Accept(Raw("L1-GLUE-01", "state", (ushort)1, T0.AddMinutes(5)), T0.AddMinutes(5)));
    }

    [Fact]
    public void Unknown_tag_is_ignored()
    {
        Assert.Empty(_n.Accept(Raw("L1-GLUE-01", "pressure", 3.0), T0));
    }

    // ---------- helpers ----------

    private static Outgoing Single(IReadOnlyList<Outgoing> messages) => Assert.Single(messages);

    private static RawValue Raw(string machine, string tag, object? value, DateTime? ts = null, DataQuality quality = DataQuality.Good)
        => new(machine, tag, value, quality, ts ?? T0, machine == "L1-GLUE-01" ? "modbus" : "opcua");

    private static GatewayOptions Options() => new()
    {
        Gateway = new GatewayIdentity { Id = "GW01", Site = "VN-BN01", Area = "SMT-ASSY", Line = "L1" },
        Devices =
        [
            new()
            {
                Machine = "L1-REFLOW-01", Protocol = "opcua",
                Tags =
                [
                    new() { Name = "peakTemp", Kind = "process", Deadband = 0.5 },
                    new() { Name = "counter", Kind = "count" },
                    new() { Name = "state", Kind = "state", Map = StateMap() },
                ],
            },
            new()
            {
                Machine = "L1-GLUE-01", Protocol = "modbus",
                Tags =
                [
                    new() { Name = "counter", Kind = "count", Type = RegisterTypes.UInt32, WordOrder = WordOrder.CDAB },
                    new() { Name = "state", Kind = "state", Map = StateMap() },
                    new() { Name = "alarm", Kind = "alarm", ZeroMeansNone = true },
                    new() { Name = "glueTemp", Kind = "process", Type = RegisterTypes.Float32 },
                ],
            },
        ],
    };

    private static Dictionary<string, string> StateMap()
        => new() { ["1"] = "Running", ["2"] = "Idle", ["3"] = "Down", ["4"] = "Setup", ["5"] = "PlannedStop" };
}
