using MiniMes.Domain.Andon;
using MiniMes.Domain.Common;
using S = MiniMes.Domain.Andon.AndonStatus;

namespace MiniMes.UnitTests.Andon;

// Spec M4 step 7: every transition of AndonCall (2.9), including acknowledging twice, resolving a closed call and
// resolving without a reason.
public class AndonCallTests
{
    private static readonly DateTime Raised = new(2026, 10, 27, 2, 42, 20, DateTimeKind.Utc);
    private static readonly DateTime Later = Raised.AddMinutes(2);
    private const int Reason = 7;

    public enum Action { Acknowledge, Resolve, Cancel }

    // Written out by hand from spec 2.9 (not derived from the code).
    public static TheoryData<S, Action, S?> Matrix() => new()
    {
        { S.Raised, Action.Acknowledge, S.Acknowledged },
        { S.Raised, Action.Resolve, S.Resolved },
        { S.Raised, Action.Cancel, S.Cancelled },
        { S.Acknowledged, Action.Acknowledge, null },
        { S.Acknowledged, Action.Resolve, S.Resolved },
        { S.Acknowledged, Action.Cancel, S.Cancelled },
        { S.Resolved, Action.Acknowledge, null },
        { S.Resolved, Action.Resolve, null },
        { S.Resolved, Action.Cancel, null },
        { S.Cancelled, Action.Acknowledge, null },
        { S.Cancelled, Action.Resolve, null },
        { S.Cancelled, Action.Cancel, null },
    };

    [Theory]
    [MemberData(nameof(Matrix))]
    public void Transition_matrix(S from, Action action, S? expected)
    {
        var call = InStatus(from);

        var act = () => Apply(call, action);

        if (expected is null)
        {
            Assert.Throws<DomainException>(act);
            Assert.Equal(from, call.Status);   // nothing changed
        }
        else
        {
            act();
            Assert.Equal(expected, call.Status);
        }
    }

    [Fact]
    public void New_call_is_raised_and_open()
    {
        var call = new AndonCall(1, AndonType.Machine, AndonCall.SystemUser, Raised);

        Assert.Equal((S.Raised, true, "system", Raised), (call.Status, call.IsOpen, call.RaisedBy, call.RaisedAt));
        Assert.Null(call.AckAt);
    }

    [Fact]
    public void Acknowledge_records_who_and_when()
    {
        var call = InStatus(S.Raised);

        call.Acknowledge("sup01", Later);

        Assert.Equal(("sup01", Later), (call.AckBy, call.AckAt));
        Assert.True(call.IsOpen);
    }

    [Fact]
    public void Acknowledging_twice_is_refused_and_keeps_the_first_responder()
    {
        var call = InStatus(S.Raised);
        call.Acknowledge("sup01", Later);

        Assert.Throws<DomainException>(() => call.Acknowledge("mt01", Later.AddSeconds(5)));
        Assert.Equal(("sup01", Later), (call.AckBy, call.AckAt));
    }

    [Fact]
    public void Resolve_after_acknowledge_keeps_the_ack_and_stores_reason_and_note()
    {
        var call = InStatus(S.Raised);
        call.Acknowledge("sup01", Later);

        call.Resolve("mt01", Reason, "  nozzle 3 replaced ", Later.AddMinutes(7));

        Assert.Equal(("sup01", Later), (call.AckBy, call.AckAt));
        Assert.Equal(("mt01", Later.AddMinutes(7), Reason, "nozzle 3 replaced"),
            (call.ResolvedBy, call.ResolvedAt, call.DowntimeReasonId, call.ResolutionNote));
        Assert.False(call.IsOpen);
    }

    // Fixed at once without a separate acknowledge: reaction time (AckAt - RaisedAt) must not be null.
    [Fact]
    public void Resolve_straight_from_raised_fills_the_acknowledge()
    {
        var call = InStatus(S.Raised);

        call.Resolve("mt01", Reason, null, Later);

        Assert.Equal(("mt01", Later), (call.AckBy, call.AckAt));
        Assert.Null(call.ResolutionNote);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Resolve_without_a_downtime_reason_is_refused(int reasonId)
    {
        var call = InStatus(S.Acknowledged);

        Assert.Throws<DomainException>(() => call.Resolve("mt01", reasonId, null, Later));
        Assert.Equal(S.Acknowledged, call.Status);
    }

    [Fact]
    public void Resolve_with_a_note_over_the_limit_is_refused()
    {
        var call = InStatus(S.Acknowledged);

        Assert.Throws<DomainException>(() => call.Resolve("mt01", Reason, new string('x', AndonCall.NoteMaxLength + 1), Later));
    }

    // Raised by mistake: closed without a reason, so it never shows up in the downtime Pareto.
    [Fact]
    public void Cancel_closes_without_a_reason()
    {
        var call = InStatus(S.Raised);

        call.Cancel("sup01", Later);

        Assert.Equal((S.Cancelled, false, "sup01", Later), (call.Status, call.IsOpen, call.ResolvedBy, call.ResolvedAt));
        Assert.Null(call.DowntimeReasonId);
    }

    private static AndonCall InStatus(S status)
    {
        var call = new AndonCall(1, AndonType.Machine, "EMP00123", Raised);
        switch (status)
        {
            case S.Acknowledged: call.Acknowledge("sup01", Raised.AddMinutes(1)); break;
            case S.Resolved: call.Resolve("mt01", Reason, null, Raised.AddMinutes(1)); break;
            case S.Cancelled: call.Cancel("sup01", Raised.AddMinutes(1)); break;
        }
        return call;
    }

    private static void Apply(AndonCall call, Action action)
    {
        switch (action)
        {
            case Action.Acknowledge: call.Acknowledge("sup01", Later); break;
            case Action.Resolve: call.Resolve("mt01", Reason, null, Later); break;
            case Action.Cancel: call.Cancel("sup01", Later); break;
        }
    }
}
