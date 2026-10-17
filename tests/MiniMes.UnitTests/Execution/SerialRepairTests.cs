using MiniMes.Domain.Common;
using MiniMes.Domain.Execution;
using MiniMes.Domain.Planning;

namespace MiniMes.UnitTests.Execution;

// Spec M3 step 4: Serial.CompleteRepair (repair / rework) and Serial.Scrap. Routing 10 SMT, 20 ASSY, 30 TEST, 40 PACK.
public class SerialRepairTests
{
    private static readonly int[] Routing = [10, 20, 30, 40];

    // Passed SMT and ASSY, NG at TEST (CurrentSeq stays 20), checked in at Repair.
    private static Serial FailedAtTest(bool checkedIn = true)
    {
        var s = new Serial("PCBA2610170000011", 1, itemId: 1);
        s.ApplyScan(10, false, ScanResult.Ok);
        s.ApplyScan(20, false, ScanResult.Ok);
        s.ApplyScan(30, false, ScanResult.Ng);
        if (checkedIn) s.CheckInRepair();
        return s;
    }

    [Theory]
    [InlineData(null, 20)]   // repair: retest the failed step (30 is next again)
    [InlineData(30, 20)]     // rework from the failed step itself = the same thing
    [InlineData(20, 10)]     // rework from ASSY
    [InlineData(10, 0)]      // rework from SMT (spec case 4d)
    public void Repair_returns_the_board_to_the_failed_or_an_earlier_step(int? returnToSeq, int expectedCurrentSeq)
    {
        var board = FailedAtTest();

        board.CompleteRepair(returnToSeq, Routing);

        Assert.Equal((SerialStatus.InProcess, expectedCurrentSeq), (board.Status, board.CurrentSeq));
    }

    [Theory]
    [InlineData(40)]   // forward: would skip the retest of the failed step
    [InlineData(15)]   // not a step of the routing
    public void Rework_step_must_be_an_earlier_routing_step(int returnToSeq)
    {
        var board = FailedAtTest();

        Assert.Throws<DomainException>(() => board.CompleteRepair(returnToSeq, Routing));
        Assert.Equal((SerialStatus.Repair, 20), (board.Status, board.CurrentSeq));
    }

    [Fact]
    public void Repair_needs_the_check_in_scan_first()
        => Assert.Throws<DomainException>(() => FailedAtTest(checkedIn: false).CompleteRepair(null, Routing));

    [Fact]
    public void A_repaired_board_cannot_be_completed_twice()
    {
        var board = FailedAtTest();
        board.CompleteRepair(null, Routing);

        Assert.Throws<DomainException>(() => board.CompleteRepair(null, Routing));
    }

    [Theory]
    [InlineData(SerialStatus.Created)]
    [InlineData(SerialStatus.InProcess)]
    [InlineData(SerialStatus.Ng)]
    [InlineData(SerialStatus.Repair)]
    public void Unfinished_boards_can_be_scrapped(SerialStatus status)
    {
        var board = new Serial("PCBA2610170000011", 1, itemId: 1);
        if (status != SerialStatus.Created) board.ApplyScan(10, false, status == SerialStatus.InProcess ? ScanResult.Ok : ScanResult.Ng);
        if (status == SerialStatus.Repair) board.CheckInRepair();

        board.Scrap("  Burnt pad U3  ");

        Assert.Equal((SerialStatus.Scrapped, "Burnt pad U3"), (board.Status, board.ScrapReason));
    }

    [Fact]
    public void Finished_boards_cannot_be_scrapped()
    {
        var completed = new Serial("PCBA2610170000011", 1, itemId: 1);
        completed.ApplyScan(10, isLastStep: true, ScanResult.Ok);
        var scrapped = new Serial("PCBA2610170000029", 1, itemId: 1);
        scrapped.Scrap("Burnt");

        Assert.Throws<DomainException>(() => completed.Scrap("Late"));
        Assert.Throws<DomainException>(() => scrapped.Scrap("Again"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Scrap_needs_a_reason(string reason)
        => Assert.Throws<DomainException>(() => new Serial("PCBA2610170000011", 1, itemId: 1).Scrap(reason));
}
