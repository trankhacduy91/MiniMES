using Microsoft.Extensions.Time.Testing;
using MiniMes.Domain.Andon;

namespace MiniMes.UnitTests.Andon;

// [v2] Spec M4 step 7b: escalation with a FakeTimeProvider, against the seed rules of 2.9b. Each "tick" does what the
// watcher does: due level from the time waited since RaisedAt, then Escalate.
public class EscalationTests
{
    private static readonly AndonEscalationRule[] SeedRules =
    [
        new(AndonType.Machine, 0, 0, "Supervisor,Maintenance"),
        new(AndonType.Machine, 1, 3, "Admin"),
        new(AndonType.Machine, 2, 10, "Admin"),
        new(AndonType.Quality, 1, 5, "QC"),
    ];

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 27, 2, 0, 0, TimeSpan.Zero));

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    private AndonCall Raise(AndonType type = AndonType.Machine) => new(1, type, AndonCall.SystemUser, Now);

    private bool Tick(AndonCall call) => call.Escalate(AndonEscalation.DueLevel(SeedRules, call.Type, Now - call.RaisedAt), Now);

    // raise -> +2 min level 0 -> 3 min level 1 -> 10 min level 2 (minutes counted from RaisedAt, as AfterMinutes is).
    [Fact]
    public void Unacknowledged_call_climbs_level_0_then_1_then_2()
    {
        var call = Raise();

        _clock.Advance(TimeSpan.FromMinutes(2));
        Assert.False(Tick(call));
        Assert.Equal(0, call.EscalationLevel);

        _clock.Advance(TimeSpan.FromMinutes(1));                         // 3:00
        Assert.True(Tick(call));
        Assert.Equal((1, Now), (call.EscalationLevel, call.LastEscalatedAt));

        _clock.Advance(TimeSpan.FromMinutes(7) - TimeSpan.FromSeconds(1)); // 9:59
        Assert.False(Tick(call));
        Assert.Equal(1, call.EscalationLevel);

        _clock.Advance(TimeSpan.FromSeconds(1));                         // 10:00
        Assert.True(Tick(call));
        Assert.Equal((2, Now), (call.EscalationLevel, call.LastEscalatedAt));
    }

    // Acknowledge stops escalation; the level reached stays for the KPI.
    [Fact]
    public void Acknowledge_at_minute_4_stops_before_level_2()
    {
        var call = Raise();
        _clock.Advance(TimeSpan.FromMinutes(3));
        Assert.True(Tick(call));

        _clock.Advance(TimeSpan.FromMinutes(1));                         // 4:00
        call.Acknowledge("sup01", Now);
        _clock.Advance(TimeSpan.FromMinutes(20));

        Assert.False(Tick(call));
        Assert.Equal(1, call.EscalationLevel);
    }

    // The watcher runs every 5 s: the same level again tells nobody twice.
    [Fact]
    public void Escalate_twice_to_the_same_level_returns_false_the_second_time()
    {
        var call = Raise();
        _clock.Advance(TimeSpan.FromMinutes(3));
        var first = Now;

        Assert.True(call.Escalate(1, Now));
        _clock.Advance(TimeSpan.FromSeconds(5));
        Assert.False(call.Escalate(1, Now));
        Assert.Equal((1, first), (call.EscalationLevel, call.LastEscalatedAt));
    }

    [Fact]
    public void Escalate_never_goes_down()
    {
        var call = Raise();
        Assert.True(call.Escalate(2, Now));

        Assert.False(call.Escalate(1, Now));
        Assert.Equal(2, call.EscalationLevel);
    }

    // After an API restart (or a long pause) the watcher jumps straight to the level due: no replay of level 1.
    [Fact]
    public void Late_tick_goes_straight_to_the_level_due()
    {
        var call = Raise();
        _clock.Advance(TimeSpan.FromMinutes(15));

        Assert.True(Tick(call));
        Assert.Equal(2, call.EscalationLevel);
    }

    [Theory]
    [InlineData(AndonStatus.Acknowledged)]
    [InlineData(AndonStatus.Resolved)]
    [InlineData(AndonStatus.Cancelled)]
    public void Only_a_raised_call_escalates(AndonStatus status)
    {
        var call = Raise();
        switch (status)
        {
            case AndonStatus.Acknowledged: call.Acknowledge("sup01", Now); break;
            case AndonStatus.Resolved: call.Resolve("mt01", 7, null, Now); break;
            case AndonStatus.Cancelled: call.Cancel("sup01", Now); break;
        }
        _clock.Advance(TimeSpan.FromMinutes(30));

        Assert.False(Tick(call));
        Assert.Equal((0, null), (call.EscalationLevel, call.LastEscalatedAt));
    }

    // Each type has its own table: Quality goes to level 1 at 5 min (not 3), and never to 2; Material has no rule.
    [Theory]
    [InlineData(AndonType.Machine, 2.9, 0)]
    [InlineData(AndonType.Machine, 3, 1)]
    [InlineData(AndonType.Machine, 10, 2)]
    [InlineData(AndonType.Quality, 4.9, 0)]
    [InlineData(AndonType.Quality, 5, 1)]
    [InlineData(AndonType.Quality, 60, 1)]
    [InlineData(AndonType.Material, 60, 0)]
    [InlineData(AndonType.Other, 60, 0)]
    public void Due_level_follows_the_rules_of_the_type(AndonType type, double minutes, int expected)
    {
        Assert.Equal(expected, AndonEscalation.DueLevel(SeedRules, type, TimeSpan.FromMinutes(minutes)));
    }

    [Fact]
    public void Rule_roles_are_split_on_commas()
    {
        Assert.Equal(["Supervisor", "Maintenance"], SeedRules[0].Roles);
    }
}
