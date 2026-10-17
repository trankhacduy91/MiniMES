using MiniMes.Domain.Common;
using MiniMes.Domain.Execution;
using MiniMes.Domain.Planning;

namespace MiniMes.UnitTests.Execution;

// Spec M3 step 1: the log rows refuse what would make them useless as evidence.
public class ExecutionEntityTests
{
    private static readonly DateTime Now = new(2026, 10, 18, 1, 0, 0, DateTimeKind.Utc);
    private static Serial Board() => new("PCBA2610170000049", workOrderId: 3, itemId: 1);

    [Fact]
    public void Ng_scan_needs_a_defect_code_and_ok_has_none()
    {
        Assert.Throws<DomainException>(() => new ScanLog(Board(), 1, 30, ScanResult.Ng, null, "EMP00123", ScanSource.Kiosk, Now));
        Assert.Throws<DomainException>(() => new ScanLog(Board(), 1, 30, ScanResult.Ok, "SLD-BRG", "EMP00123", ScanSource.Kiosk, Now));

        var log = new ScanLog(Board(), 1, 30, ScanResult.Ng, "SLD-BRG", "SIM-TEST-01", ScanSource.Machine, Now);
        Assert.Equal(("PCBA2610170000049", 3, 30, "SLD-BRG"), (log.SerialNo, log.WorkOrderId, log.Seq, log.DefectCode));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Repair_needs_the_action_taken(string action)
        => Assert.Throws<DomainException>(() => new RepairLog(Board(), 5, "SLD-BRG", action, null, null, "EMP00456", Now));

    [Fact]
    public void Repair_keeps_the_replaced_part_and_the_rework_step()
    {
        var log = new RepairLog(Board(), 5, "CMP-WRG", " Replace U3 ", " IC-MCU-QFN lot L2610-07 ", returnToSeq: 10, "EMP00456", Now);

        Assert.Equal(("Replace U3", "IC-MCU-QFN lot L2610-07", (int?)10), (log.Action, log.ComponentReplaced, log.ReturnToSeq));
    }

    [Fact]
    public void A_session_closes_once()
    {
        var session = new StationSession(stationId: 2, workOrderId: 3, "EMP00123", Now);
        Assert.True(session.IsOpen);

        session.Close(Now.AddHours(8));

        Assert.Equal(Now.AddHours(8), session.ClosedAt);
        Assert.Throws<DomainException>(() => session.Close(Now.AddHours(9)));
    }

    // Step 5: a station runs only a WO of its own line that is on the line (the M2 dispatch list).
    [Theory]
    [InlineData(1, WorkOrderStatus.Released, true)]
    [InlineData(1, WorkOrderStatus.InProgress, true)]
    [InlineData(1, WorkOrderStatus.OnHold, true)]      // log in allowed; route check blocks the scans
    [InlineData(2, WorkOrderStatus.Released, false)]   // another line
    [InlineData(1, WorkOrderStatus.Draft, false)]
    [InlineData(1, WorkOrderStatus.Completed, false)]
    [InlineData(1, WorkOrderStatus.Closed, false)]
    [InlineData(1, WorkOrderStatus.Cancelled, false)]
    public void A_station_runs_only_an_order_of_its_line_that_is_on_the_line(int woLineId, WorkOrderStatus status, bool allowed)
    {
        var check = Record.Exception(() => StationSession.EnsureCanRun(stationLineId: 1, woLineId, "WO-2610-00001", status));

        Assert.Equal(allowed, check is null);
        if (!allowed) Assert.IsType<DomainException>(check);
    }
}
