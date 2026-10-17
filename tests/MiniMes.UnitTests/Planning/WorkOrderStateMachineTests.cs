using MiniMes.Domain.Common;
using MiniMes.Domain.Planning;
using S = MiniMes.Domain.Planning.WorkOrderStatus;
using A = MiniMes.Domain.Planning.WorkOrderAction;

namespace MiniMes.UnitTests.Planning;

// Spec M2 step 1: every (status x action) cell, plus the special cases listed in the spec (PrintLabels added in step 6).
public class WorkOrderStateMachineTests
{
    private static readonly DateTime Now = new(2026, 10, 1, 1, 0, 0, DateTimeKind.Utc);

    // The legal transitions of spec 2.1, written out by hand (not derived from AllowedActions, or the test
    // would only compare the code with itself). InProgress here has no output yet.
    private static readonly Dictionary<S, (A Action, S To)[]> Legal = new()
    {
        [S.Draft] = [(A.Edit, S.Draft), (A.Release, S.Released), (A.Cancel, S.Cancelled), (A.ChangePriority, S.Draft)],
        [S.Released] = [(A.Start, S.InProgress), (A.Cancel, S.Cancelled), (A.ChangePriority, S.Released), (A.PrintLabels, S.Released)],
        [S.InProgress] = [(A.Hold, S.OnHold), (A.Complete, S.Completed), (A.Cancel, S.Cancelled), (A.ChangePriority, S.InProgress),
            (A.PrintLabels, S.InProgress)],
        [S.OnHold] = [(A.Resume, S.InProgress), (A.ChangePriority, S.OnHold)],
        [S.Completed] = [(A.Close, S.Closed)],
        [S.Closed] = [],
        [S.Cancelled] = [],
    };

    public static TheoryData<S, A> AllCells()
    {
        var data = new TheoryData<S, A>();
        foreach (var status in Enum.GetValues<S>())
            foreach (var action in Enum.GetValues<A>())
                data.Add(status, action);
        return data;
    }

    [Theory]
    [MemberData(nameof(AllCells))]
    public void Transition_matrix(S from, A action)
    {
        var wo = WorkOrders.InStatus(from);
        var legal = Legal[from].Where(t => t.Action == action).Select(t => (S?)t.To).SingleOrDefault();

        if (legal is { } to)
        {
            Apply(wo, action);
            Assert.Equal(to, wo.Status);
        }
        else
        {
            Assert.Throws<DomainException>(() => Apply(wo, action));
            Assert.Equal(from, wo.Status);   // a refused action changes nothing
        }
        // The UI enables buttons from AllowedActions: it must match what the methods accept.
        Assert.Equal(legal is not null, WorkOrders.InStatus(from).AllowedActions.Contains(action));
    }

    private static void Apply(WorkOrder wo, A action)
    {
        switch (action)
        {
            case A.Edit: wo.UpdatePlan(lineId: 2, plannedQty: 150, Now, Now.AddHours(4), requireFai: false); break;
            case A.Release: WorkOrders.Release(wo); break;
            case A.Start: wo.Start(Now); break;
            case A.Hold: wo.Hold("Suspect IC lot X moisture"); break;
            case A.Resume: wo.Resume(); break;
            case A.Complete: wo.Complete("Customer accepts a short order", Now); break;
            case A.Close: wo.Close(); break;
            case A.Cancel: wo.Cancel("Created on the wrong line"); break;
            case A.ChangePriority: wo.ChangePriority(1); break;
            case A.PrintLabels: wo.EnsureCanPrintLabels(); break;
            default: throw new ArgumentOutOfRangeException(nameof(action));
        }
    }

    // ---------- Cancel / Complete (rules R7, R10) ----------

    [Fact]
    public void Cancel_after_output_is_refused_and_not_offered()
    {
        var wo = WorkOrders.InStatus(S.InProgress);
        wo.Operations[0].RecordOutput(good: 1, ng: 0);

        var ex = Assert.Throws<DomainException>(() => wo.Cancel("Too late"));

        Assert.Contains("output", ex.Message);
        Assert.Equal(S.InProgress, wo.Status);
        Assert.DoesNotContain(A.Cancel, wo.AllowedActions);
    }

    [Fact]
    public void Ng_output_alone_also_blocks_cancel()
    {
        var wo = WorkOrders.InStatus(S.InProgress);
        wo.Operations[^1].RecordOutput(good: 0, ng: 1);

        Assert.Throws<DomainException>(() => wo.Cancel("Too late"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    public void Complete_below_planned_qty_without_a_reason_is_refused(string? reason)
    {
        var wo = WorkOrders.InStatus(S.InProgress);
        wo.Operations[^1].RecordOutput(good: 197, ng: 0);

        var ex = Assert.Throws<DomainException>(() => wo.Complete(reason, Now));

        Assert.Contains("197 of 200", ex.Message);
        Assert.Equal(S.InProgress, wo.Status);
    }

    [Fact]
    public void Complete_with_planned_qty_good_at_the_last_step_needs_no_reason()
    {
        var wo = WorkOrders.InStatus(S.InProgress);
        wo.Operations[0].RecordOutput(good: 50, ng: 0);      // earlier steps do not count (R10)
        wo.Operations[^1].RecordOutput(good: 200, ng: 3);

        wo.Complete(manualReason: null, Now);

        Assert.Equal((S.Completed, (string?)null, (DateTime?)Now), (wo.Status, wo.CompleteReason, wo.CompletedAt));
    }

    [Fact]
    public void Good_quantity_at_an_earlier_step_does_not_complete_the_order()
    {
        var wo = WorkOrders.InStatus(S.InProgress);
        wo.Operations[0].RecordOutput(good: 200, ng: 0);

        Assert.Equal(0, wo.QtyGoodLastStep);
        Assert.Throws<DomainException>(() => wo.Complete(null, Now));
    }

    [Theory]
    [InlineData("Hold")]
    [InlineData("Cancel")]
    public void Hold_and_cancel_need_a_reason(string action)
    {
        var wo = WorkOrders.InStatus(S.InProgress);

        Assert.Throws<DomainException>(() => { if (action == "Hold") wo.Hold(" "); else wo.Cancel(""); });
    }

    [Fact]
    public void Resume_clears_the_hold_reason()
    {
        var wo = WorkOrders.InStatus(S.InProgress);
        wo.Hold("Waiting for engineering");
        Assert.Equal("Waiting for engineering", wo.HoldReason);

        wo.Resume();

        Assert.Null(wo.HoldReason);
    }

    // ---------- Release snapshot (R2, R16, R17) ----------

    [Fact]
    public void Release_copies_values_and_computes_required_qty()
    {
        var wo = WorkOrders.Draft(plannedQty: 200);

        WorkOrders.Release(wo);

        Assert.Equal((1, 2, (DateTime?)Now), (wo.BomVersion, wo.RoutingVersion, wo.ReleasedAt));
        var res = Assert.Single(wo.Materials, m => m.ComponentItemId == WorkOrders.Resistor);
        Assert.Equal(2472.48m, res.RequiredQty);             // 200 x 12.3624 (12 x 1.02 x 1.01 per board), spec 1.2.4
        Assert.Equal([10, 20, 30, 40], wo.Operations.Select(o => o.Seq));   // ordered even if given unordered
        Assert.Equal("/sop/PCB-A/TEST-v1.pdf", wo.Operations.Single(o => o.Seq == 30).SopUrl);
        Assert.True(wo.Operations.Single(o => o.Seq == 30).IsInspection);
    }

    [Theory]
    [InlineData(true, FaiStatus.Pending)]
    [InlineData(false, FaiStatus.NotRequired)]
    public void Release_sets_fai_status_from_require_fai(bool requireFai, FaiStatus expected)
    {
        var wo = WorkOrders.Draft(requireFai: requireFai);
        Assert.Null(wo.FaiStatus);                            // undecided while Draft

        WorkOrders.Release(wo);

        Assert.Equal(expected, wo.FaiStatus);
    }

    [Fact]
    public void Release_without_materials_or_steps_is_refused()
    {
        Assert.Throws<DomainException>(() => WorkOrders.Draft().Release(1, 1, [], WorkOrders.Steps, Now));
        Assert.Throws<DomainException>(() => WorkOrders.Draft().Release(1, 1, WorkOrders.Materials, [], Now));
    }

    [Fact]
    public void Material_consumed_at_a_step_missing_from_the_routing_is_refused()
    {
        var wo = WorkOrders.Draft();

        var ex = Assert.Throws<DomainException>(() =>
            wo.Release(1, 1, [new MaterialSnapshot(WorkOrders.Resistor, OperationSeq: 25, 12m)], WorkOrders.Steps, Now));

        Assert.Contains("step 25", ex.Message);
        Assert.Equal(S.Draft, wo.Status);
        Assert.Empty(wo.Materials);
    }

    [Fact]
    public void Duplicate_step_numbers_are_refused()
    {
        var step = WorkOrders.Steps[0];

        Assert.Throws<DomainException>(() => WorkOrders.Draft().Release(1, 1, WorkOrders.Materials, [step, step], Now));
    }

    // ---------- FAI (v2) ----------

    [Fact]
    public void Assign_fai_serial_twice_keeps_the_first_one()
    {
        var wo = WorkOrders.InStatus(S.InProgress);

        wo.AssignFaiSerial("PCBA2610010000011");
        wo.AssignFaiSerial("PCBA2610010000029");

        Assert.Equal("PCBA2610010000011", wo.FaiSerialNo);
    }

    [Fact]
    public void Failed_fai_clears_the_sample_and_stays_pending()
    {
        var wo = WorkOrders.InStatus(S.InProgress);
        wo.AssignFaiSerial("PCBA2610010000011");

        wo.RecordFaiResult(passed: false);

        Assert.Equal((FaiStatus.Pending, (string?)null), (wo.FaiStatus, wo.FaiSerialNo));
        wo.AssignFaiSerial("PCBA2610010000029");             // the next serial becomes the new sample
        Assert.Equal("PCBA2610010000029", wo.FaiSerialNo);
    }

    [Fact]
    public void Passed_fai_ends_the_gate()
    {
        var wo = WorkOrders.InStatus(S.InProgress);
        wo.AssignFaiSerial("PCBA2610010000011");

        wo.RecordFaiResult(passed: true);

        Assert.Equal(FaiStatus.Passed, wo.FaiStatus);
        Assert.Throws<DomainException>(() => wo.AssignFaiSerial("PCBA2610010000029"));
        Assert.Throws<DomainException>(() => wo.RecordFaiResult(passed: false));
    }

    [Fact]
    public void Fai_cannot_be_judged_without_a_sample_or_when_not_required()
    {
        Assert.Throws<DomainException>(() => WorkOrders.InStatus(S.InProgress).RecordFaiResult(true));

        var noFai = WorkOrders.Draft(requireFai: false);
        WorkOrders.Release(noFai);
        Assert.Throws<DomainException>(() => noFai.AssignFaiSerial("PCBA2610010000011"));
        Assert.Throws<DomainException>(() => WorkOrders.Draft().AssignFaiSerial("PCBA2610010000011"));
    }

    // ---------- Priority (v2) and plan ----------

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    public void Priority_outside_1_to_9_is_refused(int priority)
    {
        var wo = WorkOrders.Draft();

        Assert.Throws<DomainException>(() => wo.ChangePriority(priority));
        Assert.Throws<DomainException>(() => WorkOrders.Draft(priority: priority));
        Assert.Equal(WorkOrder.DefaultPriority, wo.Priority);
    }

    [Fact]
    public void Priority_cannot_change_once_closed()
    {
        Assert.Throws<DomainException>(() => WorkOrders.InStatus(S.Closed).ChangePriority(1));
    }

    [Theory]
    [InlineData(0, 4)]
    [InlineData(-5, 4)]
    [InlineData(200, 0)]
    [InlineData(200, -1)]
    public void Zero_qty_or_end_not_after_start_is_refused(int qty, int hours)
    {
        Assert.Throws<DomainException>(() => WorkOrders.Draft(plannedQty: qty, hours: hours));
        Assert.Throws<DomainException>(() => WorkOrders.Draft().UpdatePlan(1, qty, Now, Now.AddHours(hours), true));
    }

    [Fact]
    public void Edit_changes_the_plan_while_draft()
    {
        var wo = WorkOrders.Draft(plannedQty: 220);

        wo.UpdatePlan(lineId: 2, plannedQty: 200, Now, Now.AddHours(4), requireFai: false);

        Assert.Equal((2, 200, false), (wo.LineId, wo.PlannedQty, wo.RequireFai));
    }

    [Fact]
    public void New_serial_starts_created_before_the_first_step()
    {
        var serial = new Serial("PCBA2610010000011", workOrderId: 1, itemId: 2);

        Assert.Equal((SerialStatus.Created, 0), (serial.Status, serial.CurrentSeq));
        Assert.Throws<DomainException>(() => new Serial(new string('X', Serial.SerialNoMaxLength + 1), 1, 2));
    }
}

// Builds work orders in any status through the real transitions (no reflection), PCB-A x 200 by default.
internal static class WorkOrders
{
    public const int Resistor = 4;
    private static readonly DateTime Start = new(2026, 10, 1, 1, 0, 0, DateTimeKind.Utc);

    public static readonly MaterialSnapshot[] Materials =
    [
        new(ComponentItemId: 3, OperationSeq: 10, QtyPerUnit: 1.01505m),     // PCB-BARE: 1 x 1.005 x 1.01
        new(Resistor, OperationSeq: 10, QtyPerUnit: 12.3624m),             // RES-10K-0402: 12 x 1.02 x 1.01
        new(ComponentItemId: 9, OperationSeq: 40, QtyPerUnit: 1m),          // PACK-BAG
    ];

    // Deliberately not in Seq order: Release must sort.
    public static readonly RoutingStepSnapshot[] Steps =
    [
        new(30, "TEST", 3, 45m, IsInspection: true, IsMandatory: true, "/sop/PCB-A/TEST-v1.pdf"),
        new(10, "SMT", 1, 30m, false, true, "/sop/PCB-A/SMT-v1.pdf"),
        new(20, "ASSY", 2, 60m, false, true, null),
        new(40, "PACK", 4, 20m, false, true, "/sop/PCB-A/PACK-v1.pdf"),
    ];

    public static WorkOrder Draft(int plannedQty = 200, int hours = 4, int priority = WorkOrder.DefaultPriority, bool requireFai = true)
        => WorkOrder.Create("WO-2610-00001", itemId: 1, lineId: 1, plannedQty, Start, Start.AddHours(hours), priority, requireFai);

    public static void Release(WorkOrder wo) => wo.Release(bomVersion: 1, routingVersion: 2, Materials, Steps, Start);

    public static WorkOrder InStatus(WorkOrderStatus status)
    {
        var wo = Draft();
        if (status == WorkOrderStatus.Draft) return wo;
        if (status == WorkOrderStatus.Cancelled) { wo.Cancel("Created by mistake"); return wo; }

        Release(wo);
        if (status == WorkOrderStatus.Released) return wo;
        wo.Start(Start);
        if (status == WorkOrderStatus.InProgress) return wo;
        if (status == WorkOrderStatus.OnHold) { wo.Hold("Waiting for material"); return wo; }

        wo.Complete("Customer accepts 0 pcs for this test", Start);
        if (status == WorkOrderStatus.Completed) return wo;
        wo.Close();
        return wo;
    }
}
