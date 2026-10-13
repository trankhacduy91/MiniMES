using MiniMes.Domain.Common;
using MiniMes.Domain.MasterData.Calendar;
using MiniMes.Domain.MasterData.Downtime;
using MiniMes.Domain.MasterData.Equipment;
using MiniMes.Domain.MasterData.Routings;

namespace MiniMes.UnitTests.MasterData;

public class RoutingTests
{
    private static readonly DateTime Now = new(2026, 10, 13, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Duplicate_seq_is_rejected()
    {
        var routing = Routing.CreateDraft(1, 1);
        routing.AddStep(10, "SMT", 1, 30m, isInspection: false, isMandatory: true);

        Assert.Throws<DomainException>(() => routing.AddStep(10, "ASSY", 2, 60m, false, true));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Ideal_cycle_time_must_be_positive(decimal seconds)
        => Assert.Throws<DomainException>(() => Routing.CreateDraft(1, 1).AddStep(10, "SMT", 1, seconds, false, true));

    [Fact]
    public void Routing_without_steps_cannot_be_approved()
        => Assert.Throws<DomainException>(() => Routing.CreateDraft(1, 1).Approve(Now));

    [Fact]
    public void Approved_routing_is_locked()
    {
        var routing = Routing.CreateDraft(1, 1);
        routing.AddStep(10, "SMT", 1, 30m, false, true);
        routing.Approve(Now);

        Assert.Throws<DomainException>(() => routing.AddStep(20, "ASSY", 2, 60m, false, true));
    }

    [Fact]
    public void Blank_sop_url_is_stored_as_null()
        => Assert.Null(Routing.CreateDraft(1, 1).AddStep(10, "SMT", 1, 30m, false, true, sopUrl: "  ").SopUrl);
}

public class MachineTests
{
    // These characters are MQTT level separator / wildcards and would break subscriptions (M4).
    [Theory]
    [InlineData("L1/SMT")]
    [InlineData("L1+SMT")]
    [InlineData("L1#")]
    [InlineData("L1 SMT")]
    public void Topic_key_with_mqtt_special_characters_is_rejected(string key)
        => Assert.Throws<DomainException>(() => new Machine(1, "L1-SMT-01", "Pick and place", "PickAndPlace", key, MachineDataSource.Mqtt));

    [Fact]
    public void Valid_machine_is_created()
    {
        var m = new Machine(1, "L1-REFLOW-01", "Reflow oven", "ReflowOven", "L1-REFLOW-01", MachineDataSource.OpcUa);

        Assert.Equal(MachineDataSource.OpcUa, m.DataSource);
    }
}

public class ShiftTests
{
    [Theory]
    [InlineData(6, 14, false)]   // S1 06:00-14:00
    [InlineData(22, 6, true)]    // S3 22:00-06:00 runs past midnight
    public void Crosses_midnight_is_derived_from_the_times(int start, int end, bool expected)
    {
        var shift = new Shift("S", "Shift", new TimeOnly(start, 0), new TimeOnly(end, 0), WorkDays.MonToSat);

        Assert.Equal(expected, shift.CrossesMidnight);
    }

    [Fact]
    public void Start_equal_to_end_is_rejected()
        => Assert.Throws<DomainException>(() => new Shift("S", "Shift", new TimeOnly(6, 0), new TimeOnly(6, 0), WorkDays.MonToSat));

    [Fact]
    public void Shift_needs_at_least_one_day()
        => Assert.Throws<DomainException>(() => new Shift("S", "Shift", new TimeOnly(6, 0), new TimeOnly(14, 0), WorkDays.None));

    [Fact]
    public void Mon_to_sat_does_not_include_sunday()
    {
        Assert.True(WorkDays.MonToSat.HasFlag(WorkDays.Sat));
        Assert.False(WorkDays.MonToSat.HasFlag(WorkDays.Sun));
    }
}

public class DowntimeReasonTests
{
    [Fact]
    public void Unplanned_downtime_must_count_against_availability()
        => Assert.Throws<DomainException>(() => new DowntimeReason("MCH-FAULT", "Machine fault", DowntimeCategory.Unplanned, false));

    [Theory]
    [InlineData(true)]   // changeover: planned but counted, to measure and reduce it
    [InlineData(false)]  // preventive maintenance: removed from planned time
    public void Planned_downtime_may_or_may_not_count(bool counts)
        => Assert.Equal(counts, new DowntimeReason("PM", "Preventive maintenance", DowntimeCategory.Planned, counts).CountsAgainstAvailability);
}
