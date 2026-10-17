using Microsoft.Extensions.Time.Testing;
using MiniMes.Application.Machines;
using MiniMes.Contracts.Machines;
using DomainState = MiniMes.Domain.Machines.MachineState;
using WireState = MiniMes.Contracts.Machines.MachineState;

namespace MiniMes.UnitTests.Machines;

// Spec M4 step 5: Offline is inferred (30 s without a message), with a grace period while the MES itself is blind.
// FakeTimeProvider: the watchdog's minutes pass in microseconds.
public class OfflineDetectorTests
{
    private const int M = 3;
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 17, 8, 0, 0, TimeSpan.Zero));
    private readonly MachineRuntimeStore _store = new();
    private readonly OfflineDetector _detector;

    public OfflineDetectorTests() => _detector = new OfflineDetector(_clock);

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    [Fact]
    public void Silent_for_more_than_30_seconds_is_offline()
    {
        var connected = Now;
        _clock.Advance(TimeSpan.FromMinutes(1));
        Heartbeat();

        _clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Empty(Check(connected));               // exactly 30 s: not yet
        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal([M], Check(connected));
    }

    [Fact]
    public void Heartbeats_keep_a_machine_online()
    {
        var connected = Now;
        foreach (var _ in Enumerable.Range(0, 20))
        {
            _clock.Advance(TimeSpan.FromSeconds(5));
            Heartbeat();
            Assert.Empty(Check(connected));
        }
    }

    // Grace: ingest not connected (broker down) -> no verdict at all, however long the silence.
    [Fact]
    public void Nothing_is_judged_while_the_ingest_is_not_connected()
    {
        Heartbeat();
        _clock.Advance(TimeSpan.FromMinutes(2));

        Assert.Empty(Check(connectedSince: null));
    }

    // Broker back after 2 minutes: last messages are 2 minutes old, but each machine gets 30 s from the reconnect.
    [Fact]
    public void After_a_reconnect_machines_get_the_full_timeout_to_be_heard_again()
    {
        Heartbeat();
        _clock.Advance(TimeSpan.FromMinutes(2));
        var reconnected = Now;

        _clock.Advance(TimeSpan.FromSeconds(29));
        Assert.Empty(Check(reconnected));             // still in the grace period
        _clock.Advance(TimeSpan.FromSeconds(1));
        Heartbeat();                                  // the machine reconnected and spoke
        _clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Empty(Check(reconnected));
    }

    // Seeded from the DB after an API restart but never heard since: Offline once the grace period is over.
    [Fact]
    public void A_machine_never_heard_since_the_start_becomes_offline_after_the_grace()
    {
        _store.Seed(M, DomainState.Running, Now.AddHours(-1), lastCounter: 100, lastCountAt: Now.AddHours(-1));
        var connected = Now;

        _clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Empty(Check(connected));
        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal([M], Check(connected));
    }

    [Fact]
    public void An_offline_machine_is_not_reported_again()
    {
        var connected = Now;
        Heartbeat();
        _clock.Advance(TimeSpan.FromMinutes(1));
        Assert.NotNull(_store.MarkOffline(M, Now));

        Assert.Empty(Check(connected));
        Assert.Null(_store.MarkOffline(M, Now));      // second verdict changes nothing
    }

    // The heartbeat carries the state: the first one after Offline brings the machine back at once (spec 2.5),
    // on the server's clock even when the PLC's clock is behind.
    [Fact]
    public void Heartbeat_after_offline_brings_the_real_state_back()
    {
        Heartbeat();
        _clock.Advance(TimeSpan.FromMinutes(1));
        var offline = _store.MarkOffline(M, Now)!;
        _clock.Advance(TimeSpan.FromSeconds(2));

        var back = _store.Apply(M, MachineKinds.Heartbeat,
            new MachineMessage(Guid.NewGuid(), "L1-TEST-01", _clock.GetUtcNow().AddSeconds(-20), State: WireState.Running), Now);

        Assert.Equal((DomainState.Offline, MiniMes.Domain.Machines.MachineEventKind.Connection), (offline.StateChange!.State, offline.Event!.Kind));
        Assert.Equal(new StateChange(M, DomainState.Running, Now), back.Item!.StateChange);
        Assert.Equal(DomainState.Running, _store.Get(M)!.State);
    }

    // ---------- LWT report [found in step 5] ----------

    // Killed machine: the broker sends its LWT, our connection stays -> Offline after the 3 s confirmation.
    [Fact]
    public void Lwt_report_is_confirmed_when_our_connection_survives()
    {
        var connected = Now;
        _clock.Advance(TimeSpan.FromMinutes(1));
        Heartbeat();
        _store.ReportOffline(M, Now);

        _clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Empty(Check(connected));
        _clock.Advance(TimeSpan.FromSeconds(1));
        var verdict = Assert.Single(_detector.Find(_store.All(), connected));
        Assert.Equal((M, "status offline (LWT)"), (verdict.MachineId, verdict.Reason));
    }

    // Mosquitto stopped: it publishes every client's LWT, then drops the ingest too. Neither while blind nor after
    // the reconnect (a new connection, newer than the report) does the report count.
    [Fact]
    public void Lwt_report_from_a_broker_shutdown_does_not_count()
    {
        var connected = Now;
        _clock.Advance(TimeSpan.FromMinutes(1));
        Heartbeat();
        _store.ReportOffline(M, Now);

        _clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Empty(Check(connectedSince: null));    // broker down
        var reconnected = Now;
        _clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Empty(Check(reconnected));
    }

    // The machine reconnected and spoke right after its LWT: the report is outdated.
    [Fact]
    public void Lwt_report_is_dropped_once_the_machine_speaks_again()
    {
        var connected = Now;
        _clock.Advance(TimeSpan.FromMinutes(1));
        _store.ReportOffline(M, Now);
        _clock.Advance(TimeSpan.FromSeconds(1));
        Heartbeat();

        _clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Empty(Check(connected));
    }

    private void Heartbeat()
        => _store.Apply(M, MachineKinds.Heartbeat, new MachineMessage(Guid.NewGuid(), "L1-TEST-01", _clock.GetUtcNow(), State: WireState.Running), Now);

    private IReadOnlyList<int> Check(DateTime? connectedSince) => _detector.Find(_store.All(), connectedSince).Select(v => v.MachineId).ToList();
}
