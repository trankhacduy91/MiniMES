using MiniMes.Domain.Common;
using MiniMes.Domain.Execution;
using MiniMes.Domain.Planning;
using V = MiniMes.Domain.Execution.RouteVerdict;
using R = MiniMes.Domain.Execution.BlockReason;

namespace MiniMes.UnitTests.Execution;

// Spec M3 step 2: the 19 cases of the table in 2.1 (each its own test row), plus edge cases.
// Routing of the seeded PCB-A: 10 SMT, 20 ASSY, 30 TEST, 40 PACK (M1 numbers steps 10, 20, 30...).
public class RouteCheckTests
{
    private static readonly DateTime Now = new(2026, 10, 18, 2, 0, 0, DateTimeKind.Utc);
    private const int WoA = 1, WoB = 2;
    private const string S1 = "PCBA2610170000011", S2 = "PCBA2610170000029";

    private static readonly RouteStep[] Routing =
        [new(40, "PACK", 4), new(10, "SMT", 1), new(30, "TEST", 3), new(20, "ASSY", 2)];   // any order on purpose

    private static readonly StationInfo Smt = new(1, 1, false), Asm = new(2, 2, false), Test = new(3, 3, false),
        Pack = new(4, 4, false), Repair = new(5, null, true);

    private static ScanContext Ctx(StationInfo at, SerialStatus status = SerialStatus.InProcess, int cur = 0, string serialNo = S1,
        int serialWo = WoA, WorkOrderStatus wo = WorkOrderStatus.InProgress, int lastScanSecondsAgo = -1,
        ScanResult lastResult = ScanResult.Ok, ScanResult requested = ScanResult.Ok, FaiStatus fai = FaiStatus.NotRequired,
        string? faiSample = null, IReadOnlyList<RouteStep>? routing = null)
        => new(new SerialState(serialNo, serialWo, status, cur), wo, WoA, at, routing ?? Routing, fai, faiSample,
            lastScanSecondsAgo < 0 ? null : new LastScan(Now.AddSeconds(-lastScanSecondsAgo), lastResult),
            requested, Now, TimeSpan.FromSeconds(5));

    // case -> (context, verdict, reason, step done, FAI sample assigned)
    private static readonly Dictionary<int, (ScanContext C, V Verdict, R Reason, int? Step, bool Fai)> Cases = new()
    {
        [1] = (Ctx(Smt, SerialStatus.Created, cur: 0), V.Allow, R.None, 10, false),
        [2] = (Ctx(Asm, cur: 10), V.Allow, R.None, 20, false),
        [3] = (Ctx(Test, cur: 10), V.Block, R.SkippedStep, null, false),
        [4] = (Ctx(Asm, cur: 30, lastScanSecondsAgo: 40), V.Block, R.AlreadyPassed, null, false),
        [5] = (Ctx(Asm, cur: 20, lastScanSecondsAgo: 2), V.Duplicate, R.None, null, false),
        [6] = (Ctx(Asm, cur: 20, lastScanSecondsAgo: 30), V.Block, R.AlreadyPassed, null, false),
        [7] = (Ctx(Test, SerialStatus.Ng, cur: 20), V.Block, R.MustGoRepair, null, false),
        [8] = (Ctx(Repair, SerialStatus.Ng, cur: 20), V.Allow, R.None, null, false),
        [9] = (Ctx(Repair, cur: 20), V.Block, R.NotDefective, null, false),
        [10] = (Ctx(Test, cur: 20), V.Allow, R.None, 30, false),                                  // back from Repair
        [11] = (Ctx(Asm, SerialStatus.Hold, cur: 10), V.Block, R.SerialHold, null, false),
        [12] = (Ctx(Smt, SerialStatus.Scrapped), V.Block, R.SerialScrapped, null, false),
        [13] = (Ctx(Pack, SerialStatus.Completed, cur: 40, lastScanSecondsAgo: 30), V.Block, R.SerialCompleted, null, false),
        [14] = (Ctx(Asm, cur: 10, serialWo: WoB), V.Block, R.WrongWo, null, false),
        [15] = (Ctx(Asm, cur: 10, wo: WorkOrderStatus.Completed), V.Block, R.WoNotActive, null, false),
        // [v2] FAI gate
        [16] = (Ctx(Smt, SerialStatus.Created, fai: FaiStatus.Pending), V.Allow, R.None, 10, true),
        [17] = (Ctx(Smt, SerialStatus.Created, serialNo: S2, fai: FaiStatus.Pending, faiSample: S1), V.Block, R.FaiNotApproved, null, false),
        [18] = (Ctx(Asm, cur: 10, fai: FaiStatus.Pending, faiSample: S1), V.Allow, R.None, 20, false),
        [19] = (Ctx(Smt, SerialStatus.Created, serialNo: S2, fai: FaiStatus.Passed, faiSample: S1), V.Allow, R.None, 10, false),
    };

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(7)]
    [InlineData(8)] [InlineData(9)] [InlineData(10)] [InlineData(11)] [InlineData(12)] [InlineData(13)] [InlineData(14)]
    [InlineData(15)] [InlineData(16)] [InlineData(17)] [InlineData(18)] [InlineData(19)]
    public void Spec_table(int number)
    {
        var (context, verdict, reason, step, fai) = Cases[number];

        var decision = RouteCheck.Evaluate(context);

        Assert.Equal((verdict, reason, step, fai), (decision.Verdict, decision.Reason, decision.Step?.Seq, decision.AssignFaiSample));
        Assert.False(string.IsNullOrWhiteSpace(decision.Message));
    }

    // ---------- beyond the table ----------

    [Fact]
    public void Unknown_serial_is_not_found()
        => Assert.Equal(R.SerialNotFound, RouteCheck.Evaluate(Ctx(Asm) with { Serial = null, WoStatus = null }).Reason);

    [Fact]
    public void Released_order_runs_like_in_progress()
        => Assert.Equal(V.Allow, RouteCheck.Evaluate(Ctx(Smt, SerialStatus.Created, wo: WorkOrderStatus.Released)).Verdict);

    [Theory]
    [InlineData(9)]      // a work center the routing does not use
    [InlineData(null)]   // a production station never linked to a work center
    public void Station_outside_the_routing_is_blocked(int? workCenterId)
        => Assert.Equal(R.StationNotInRoute, RouteCheck.Evaluate(Ctx(new StationInfo(7, workCenterId, false), cur: 10)).Reason);

    [Fact]
    public void Skipped_step_message_names_the_expected_step()
        => Assert.Contains("20 ASSY", RouteCheck.Evaluate(Cases[3].C).Message);

    // The order matters (spec 1.4): the double trigger at PACK completes the board on the first scan; the
    // second must still be "duplicate", not "already completed".
    [Fact]
    public void Duplicate_wins_over_completed_status()
        => Assert.Equal(V.Duplicate, RouteCheck.Evaluate(Ctx(Pack, SerialStatus.Completed, cur: 40, lastScanSecondsAgo: 1)).Verdict);

    // A tester that sends OK then NG one second later (a machine bug) is not a duplicate: the result differs.
    [Fact]
    public void Different_result_inside_the_window_is_not_a_duplicate()
        => Assert.Equal(R.AlreadyPassed, RouteCheck.Evaluate(
            Ctx(Test, cur: 30, lastScanSecondsAgo: 1, lastResult: ScanResult.Ok, requested: ScanResult.Ng)).Reason);

    [Fact]
    public void Exactly_at_the_end_of_the_window_is_still_a_duplicate()
        => Assert.Equal(V.Duplicate, RouteCheck.Evaluate(Ctx(Asm, cur: 20, lastScanSecondsAgo: 5)).Verdict);

    // Rule 4 before rule 6: QC failed the first article, so RecordFaiResult(false) (M2) cleared the sample while
    // the board is still NG. It must reach Repair even though it is no longer "the sample".
    [Fact]
    public void Failed_fai_sample_can_go_to_repair()
        => Assert.Equal(V.Allow, RouteCheck.Evaluate(Ctx(Repair, SerialStatus.Ng, cur: 30, fai: FaiStatus.Pending, faiSample: null)).Verdict);

    // Rule 6 before rule 3: a second board at the wrong step is stopped for the real reason, the closed gate.
    [Fact]
    public void Fai_gate_reason_wins_over_step_reason()
        => Assert.Equal(R.FaiNotApproved, RouteCheck.Evaluate(
            Ctx(Asm, SerialStatus.Created, serialNo: S2, fai: FaiStatus.Pending, faiSample: S1)).Reason);

    // A work center used twice (test, then final test): the station takes whichever of its steps is next.
    [Fact]
    public void Work_center_used_twice_matches_the_next_of_its_steps()
    {
        RouteStep[] routing = [new(10, "SMT", 1), new(20, "TEST", 3), new(30, "ASSY", 2), new(40, "FINAL-TEST", 3)];

        Assert.Equal(20, RouteCheck.Evaluate(Ctx(Test, cur: 10, routing: routing)).Step?.Seq);
        Assert.Equal(40, RouteCheck.Evaluate(Ctx(Test, cur: 30, routing: routing)).Step?.Seq);
        Assert.Equal(R.SkippedStep, RouteCheck.Evaluate(Ctx(Test, cur: 20, routing: routing)).Reason);
    }

    // ---------- Serial.ApplyScan ----------

    private static Serial Board(SerialStatus status = SerialStatus.Created)
    {
        var s = new Serial(S1, WoA, itemId: 1);
        if (status == SerialStatus.InProcess) s.ApplyScan(10, isLastStep: false, ScanResult.Ok);
        return s;
    }

    [Fact]
    public void Ok_moves_the_board_to_the_step()
    {
        var board = Board();

        board.ApplyScan(10, isLastStep: false, ScanResult.Ok);

        Assert.Equal((SerialStatus.InProcess, 10), (board.Status, board.CurrentSeq));
    }

    [Fact]
    public void Ok_at_the_last_step_completes_the_board()
    {
        var board = Board(SerialStatus.InProcess);
        board.ApplyScan(20, false, ScanResult.Ok);
        board.ApplyScan(30, false, ScanResult.Ok);

        board.ApplyScan(40, isLastStep: true, ScanResult.Ok);

        Assert.Equal((SerialStatus.Completed, 40), (board.Status, board.CurrentSeq));
    }

    [Fact]
    public void Ng_keeps_the_step_so_the_test_is_done_again_after_repair()
    {
        var board = Board(SerialStatus.InProcess);

        board.ApplyScan(20, isLastStep: false, ScanResult.Ng);

        Assert.Equal((SerialStatus.Ng, 10), (board.Status, board.CurrentSeq));
        Assert.Throws<DomainException>(() => board.ApplyScan(20, false, ScanResult.Ok));   // NG boards go to Repair first
    }

    [Fact]
    public void A_step_already_passed_cannot_be_applied_again()
        => Assert.Throws<DomainException>(() => Board(SerialStatus.InProcess).ApplyScan(10, false, ScanResult.Ok));
}
