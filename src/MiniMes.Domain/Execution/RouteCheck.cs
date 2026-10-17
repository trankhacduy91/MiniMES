using MiniMes.Domain.Planning;

namespace MiniMes.Domain.Execution;

// Route check / interlock (spec M3 1.4, 2.1): may this board be worked at this station now? Poka-yoke in
// software: a board that skipped a step, failed a test or is on hold cannot be recorded here.
// A pure function: blocked is a normal answer with a reason, not an exception. The ORDER of the checks is part
// of the contract and is covered by tests:
//   1 exists / WO active / right WO  ->  5 duplicate  ->  2 Hold/Scrapped/Completed  ->  4 NG and Repair
//   ->  station in route  ->  6 FAI gate [v2]  ->  3 step.
public static class RouteCheck
{
    public static RouteDecision Evaluate(ScanContext c)
    {
        // Rule 1: the board and its order may be worked here at all.
        if (c.Serial is not { } serial)
            return RouteDecision.Block(BlockReason.SerialNotFound, "Serial not found.");
        if (c.WoStatus is not (WorkOrderStatus.Released or WorkOrderStatus.InProgress))
            return RouteDecision.Block(BlockReason.WoNotActive, $"Work order is {c.WoStatus}, not running.");
        if (serial.WorkOrderId != c.SessionWoId)
            return RouteDecision.Block(BlockReason.WrongWo, "Serial belongs to another work order.");

        // Rule 5 BEFORE status and step: after the first OK the board has moved on (or is Completed at the last
        // step), so a double trigger of the scanner would otherwise read as "already passed".
        if (c.LastScanHere is { } last && c.NowUtc - last.At <= c.DuplicateWindow && last.Result == c.RequestedResult)
            return new RouteDecision(RouteVerdict.Duplicate, BlockReason.None, "Duplicate scan ignored.");

        // Rule 2: suspect, destroyed or finished boards go nowhere.
        switch (serial.Status)
        {
            case SerialStatus.Hold: return RouteDecision.Block(BlockReason.SerialHold, "Serial is on HOLD.");
            case SerialStatus.Scrapped: return RouteDecision.Block(BlockReason.SerialScrapped, "Serial is scrapped.");
            case SerialStatus.Completed: return RouteDecision.Block(BlockReason.SerialCompleted, "Serial is already completed.");
        }

        // Rule 4: defective boards only at Repair, and Repair only takes defective boards. Before the FAI gate:
        // a failed first article must still reach Repair.
        var defective = serial.Status is SerialStatus.Ng or SerialStatus.Repair;
        if (c.Station.IsRepair)
            return defective
                ? new RouteDecision(RouteVerdict.Allow, BlockReason.None, "Repair check-in.")
                : RouteDecision.Block(BlockReason.NotDefective, "Serial is not defective; Repair only takes NG boards.");
        if (defective)
            return RouteDecision.Block(BlockReason.MustGoRepair, "Serial is NG: send it to Repair.");

        var steps = c.Operations.OrderBy(o => o.Seq).ToList();
        var stepsHere = steps.Where(o => o.WorkCenterId == c.Station.WorkCenterId).ToList();
        if (stepsHere.Count == 0)
            return RouteDecision.Block(BlockReason.StationNotInRoute, "This station is not in the work order's routing.");

        // Next step = the smallest Seq above CurrentSeq (routings use 10, 20, 30...). Null cannot happen for a
        // board that is not Completed, but a broken snapshot must not crash the scan.
        var expected = steps.FirstOrDefault(o => o.Seq > serial.CurrentSeq);

        // [v2] Rule 6: while the first article is pending, exactly one board may run: the sample.
        var assignFai = false;
        if (c.FaiStatus == FaiStatus.Pending)
        {
            var atFirstStep = expected is not null && expected.Seq == steps[0].Seq && stepsHere.Contains(expected);
            if (c.FaiSerialNo is null && atFirstStep && serial.CurrentSeq == 0)
                assignFai = true;                                                   // 6a: this board is the sample
            else if (c.FaiSerialNo != serial.SerialNo)
                return RouteDecision.Block(BlockReason.FaiNotApproved, "First article not approved yet (waiting for QC).");
        }

        // Rule 3: exactly the next step. A work center may appear twice in a routing (test, then retest at the
        // end): the station matches if ANY of its steps is the expected one.
        if (expected is not null && stepsHere.Contains(expected))
            return new RouteDecision(RouteVerdict.Allow, BlockReason.None, "OK") { Step = expected, AssignFaiSample = assignFai };
        if (expected is null || stepsHere.All(o => o.Seq < expected.Seq))
            return RouteDecision.Block(BlockReason.AlreadyPassed, $"Step {stepsHere[^1].Seq} {stepsHere[^1].OperationCode} already passed.");
        return RouteDecision.Block(BlockReason.SkippedStep,
            $"Expected step {expected.Seq} {expected.OperationCode}; this station does step {stepsHere.First(o => o.Seq > expected.Seq).Seq}.");
    }
}
