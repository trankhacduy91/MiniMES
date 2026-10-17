using System.Text.Json;
using MiniMes.Contracts.Machines;
using MiniMes.Domain.Common;
using MiniMes.Domain.Machines;
using DomainState = MiniMes.Domain.Machines.MachineState;
using DomainQuality = MiniMes.Domain.Machines.DataQuality;
using WireState = MiniMes.Contracts.Machines.MachineState;
using WireQuality = MiniMes.Contracts.Machines.DataQuality;

namespace MiniMes.UnitTests.Machines;

// Spec M4 step 1: the MQTT contract (topic, JSON) and the M4 entities' own rules.
public class MachineContractTests
{
    private const string Smt = "minimes/VN-BN01/SMT-ASSY/L1/L1-SMT-01";

    // ---------- topic (Unified Namespace) ----------

    [Fact]
    public void Six_level_topic_round_trips()
    {
        var topic = MachineTopic.Build("VN-BN01", "SMT-ASSY", "L1", "L1-SMT-01", MachineKinds.State);

        Assert.Equal($"{Smt}/state", topic);
        Assert.True(MachineTopic.TryParse(topic, out var p));
        Assert.Equal(new ParsedTopic("VN-BN01", "SMT-ASSY", "L1", "L1-SMT-01", "state", null), p);
    }

    [Fact]
    public void Seven_level_process_topic_carries_the_tag()
    {
        var topic = MachineTopic.BuildProcess("VN-BN01", "SMT-ASSY", "L1", "L1-REFLOW-01", "peakTemp");

        Assert.Equal("minimes/VN-BN01/SMT-ASSY/L1/L1-REFLOW-01/process/peakTemp", topic);
        Assert.True(MachineTopic.TryParse(topic, out var p));
        Assert.Equal(("L1-REFLOW-01", MachineKinds.Process, "peakTemp"), (p.Machine, p.Kind, p.Tag));
    }

    [Theory]
    [InlineData("minimes/VN-BN01/L1/L1-SMT-01/state")]                       // 5 levels: the old topic without area
    [InlineData("other/VN-BN01/SMT-ASSY/L1/L1-SMT-01/state")]                // another root
    [InlineData("minimes/VN-BN01/SMT-ASSY/L1/L1-SMT-01/state/extra")]        // 7 levels but not process
    [InlineData("minimes/VN-BN01/SMT-ASSY/L1/L1-SMT-01/process")]            // process without its tag
    [InlineData("minimes/VN-BN01/SMT-ASSY//L1-SMT-01/state")]                // empty level
    [InlineData("minimes/VN-BN01/SMT-ASSY/L1/L1-SMT-01/process/a/b")]        // 8 levels
    public void Malformed_topics_are_rejected(string topic)
        => Assert.False(MachineTopic.TryParse(topic, out _));

    // ---------- JSON ----------

    [Fact]
    public void Json_is_camel_case_with_enums_as_text()
    {
        var msg = new MachineMessage(Guid.Parse("b3f1c2d4-0000-4000-8000-000000000001"), "L1-SMT-01",
            new DateTimeOffset(2026, 10, 1, 1, 23, 45, 123, TimeSpan.Zero), State: WireState.Running);

        var json = JsonSerializer.Serialize(msg, MachineJson.Options);

        Assert.Contains("\"msgId\":\"b3f1c2d4-0000-4000-8000-000000000001\"", json);
        Assert.Contains("\"ts\":\"2026-10-01T01:23:45.123Z\"", json);   // UTC with Z, not "+00:00"
        Assert.Contains("\"state\":\"Running\"", json);
        Assert.Contains("\"quality\":\"Good\"", json);
        Assert.Contains("\"source\":\"mqtt\"", json);
        Assert.Equal(msg, JsonSerializer.Deserialize<MachineMessage>(json, MachineJson.Options));
    }

    // The example of spec 2.3b, as a gateway would send it (fields missing = null / defaults; any case).
    [Fact]
    public void Gateway_process_message_of_the_spec_reads()
    {
        const string json = """{ "msgId": "5f0c0000-0000-4000-8000-000000000002", "machine": "L1-REFLOW-01", "ts": "2026-10-11T02:15:30.250Z", "tag": "peakTemp", "value": 245.3, "quality": "good", "source": "opcua" }""";

        var msg = JsonSerializer.Deserialize<MachineMessage>(json, MachineJson.Options)!;

        Assert.Equal(("peakTemp", 245.3, WireQuality.Good, "opcua", (WireState?)null), (msg.Tag, msg.Value, msg.Quality, msg.Source, msg.State));
        Assert.Equal(TimeSpan.Zero, msg.Ts.Offset);
    }

    // A sender with a local offset still lands on the same instant, stored as UTC.
    [Fact]
    public void Timestamp_with_an_offset_is_read_as_utc()
    {
        const string json = """{ "msgId": "5f0c0000-0000-4000-8000-000000000003", "machine": "L1-SMT-01", "ts": "2026-10-11T09:15:30.250+07:00" }""";

        var ts = JsonSerializer.Deserialize<MachineMessage>(json, MachineJson.Options)!.Ts;

        Assert.Equal((new DateTime(2026, 10, 11, 2, 15, 30, 250), TimeSpan.Zero), (ts.DateTime, ts.Offset));
    }

    // The wire enums (Contracts) and the stored ones (Domain) must stay the same list: the ingest maps by name.
    [Fact]
    public void Wire_and_domain_enums_match()
    {
        Assert.Equal(Enum.GetNames<WireState>(), Enum.GetNames<DomainState>());
        Assert.Equal(Enum.GetNames<WireQuality>(), Enum.GetNames<DomainQuality>());
    }

    // ---------- entities ----------

    private static readonly DateTime T0 = new(2026, 10, 17, 1, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void A_state_segment_closes_once_and_never_before_it_starts()
    {
        var segment = new MachineStateHistory(1, DomainState.Running, T0);

        Assert.Throws<DomainException>(() => segment.Close(T0.AddSeconds(-1)));
        segment.Close(T0.AddMinutes(7));

        Assert.Equal((false, (DateTime?)T0.AddMinutes(7)), (segment.IsOpen, segment.EndAt));
        Assert.Throws<DomainException>(() => segment.Close(T0.AddMinutes(8)));
    }

    // Spec 2.3b: outside the physical range the sensor is broken (a cut wire reads -32768): stored as Bad.
    [Theory]
    [InlineData(245.3, DomainQuality.Good, DomainQuality.Good)]
    [InlineData(0, DomainQuality.Good, DomainQuality.Good)]           // the limits themselves are plausible
    [InlineData(400, DomainQuality.Uncertain, DomainQuality.Uncertain)]
    [InlineData(-32768, DomainQuality.Good, DomainQuality.Bad)]
    [InlineData(400.1, DomainQuality.Good, DomainQuality.Bad)]
    [InlineData(double.NaN, DomainQuality.Good, DomainQuality.Bad)]
    public void Tag_judges_the_quality_by_its_plausible_range(double value, DomainQuality reported, DomainQuality stored)
        => Assert.Equal(stored, new MachineTag(1, "peakTemp", "°C", 0, 400).Judge(value, reported));

    [Theory]
    [InlineData("", "°C", 0, 400)]
    [InlineData("zone/1", "°C", 0, 400)]   // '/' would break the topic
    [InlineData("peakTemp", "", 0, 400)]
    [InlineData("peakTemp", "°C", 400, 400)]
    public void Tag_master_data_is_validated(string tag, string unit, double low, double high)
        => Assert.Throws<DomainException>(() => new MachineTag(1, tag, unit, low, high));

    [Fact]
    public void Count_event_keeps_the_computed_delta()
    {
        var e = MachineEvent.Count(Guid.NewGuid(), 3, 3, CounterMath.Compute(15290, 3), 3, 0, T0, T0.AddMilliseconds(40));

        Assert.Equal((MachineEventKind.Count, 3L, (long?)3, true), (e.Kind, e.Counter!.Value, e.Delta, e.IsReset));
        Assert.Throws<DomainException>(() => MachineEvent.Alarm(Guid.Empty, 3, "E205", T0, T0));
    }
}
