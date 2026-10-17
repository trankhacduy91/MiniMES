using MiniMes.Domain.Common;

namespace MiniMes.Domain.Planning;

// A production order: one item, one quantity, one line, one time window (spec M2 1.2b).
// The status only changes through the methods below; each one checks the current status first,
// so no handler, job or screen can skip a rule (rich domain model, spec 2.1).
public class WorkOrder : AuditableEntity, IAudited
{
    public const int WoNoMaxLength = 20;
    public const int ReasonMaxLength = 500;
    public const int MinPriority = 1;   // 1 = most urgent
    public const int MaxPriority = 9;
    public const int DefaultPriority = 5;

    public int Id { get; private set; }
    public string WoNo { get; private set; }            // WO-yyMM-##### from a SQL sequence (step 3)
    public int ItemId { get; private set; }
    public int LineId { get; private set; }
    public int PlannedQty { get; private set; }         // whole units: one serial per unit
    public DateTime PlannedStart { get; private set; }  // UTC
    public DateTime PlannedEnd { get; private set; }
    public WorkOrderStatus Status { get; private set; } = WorkOrderStatus.Draft;
    public int Priority { get; private set; } = DefaultPriority;   // [v2] dispatch list order
    public bool RequireFai { get; private set; } = true;           // [v2]
    public FaiStatus? FaiStatus { get; private set; }              // [v2] null until Release
    public string? FaiSerialNo { get; private set; }               // [v2] first serial scanned while FAI is pending (M3)
    public int? BomVersion { get; private set; }                   // version of the WO item's BOM at Release
    public int? RoutingVersion { get; private set; }
    public DateTime? ReleasedAt { get; private set; }              // also tells which semi BOM versions were in effect
    public DateTime? StartedAt { get; private set; }
    public DateTime? CompletedAt { get; private set; }
    public string? HoldReason { get; private set; }
    public string? CompleteReason { get; private set; }            // only for a manual complete below the planned qty
    public string? CancelReason { get; private set; }
    public string? ExternalRef { get; private set; }               // ERP order number, reserved for M8: no logic uses it
    public byte[] RowVersion { get; private set; } = [];           // optimistic concurrency (spec 2.2)

    public List<WorkOrderMaterial> Materials { get; private set; } = [];
    public List<WorkOrderOperation> Operations { get; private set; } = [];

    private WorkOrder() { WoNo = string.Empty; }

    public static WorkOrder Create(string woNo, int itemId, int lineId, int plannedQty,
        DateTime plannedStart, DateTime plannedEnd, int priority = DefaultPriority, bool requireFai = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(woNo);
        if (woNo.Length > WoNoMaxLength) throw new DomainException($"WoNo must be at most {WoNoMaxLength} characters.");
        RequirePriority(priority);

        var wo = new WorkOrder { WoNo = woNo, ItemId = itemId, Priority = priority };
        wo.SetPlan(lineId, plannedQty, plannedStart, plannedEnd, requireFai);
        return wo;
    }

    // Any step has good or NG output. Needs Operations loaded (Include) or it is always false.
    public bool HasOutput => Operations.Any(o => o.QtyGood + o.QtyNg > 0);

    // Good quantity of the last routing step: the only count that means "finished units" (rule R10).
    public int QtyGoodLastStep => Operations.OrderBy(o => o.Seq).LastOrDefault()?.QtyGood ?? 0;

    public IReadOnlyList<WorkOrderAction> AllowedActions => Status switch
    {
        WorkOrderStatus.Draft =>
            [WorkOrderAction.Edit, WorkOrderAction.Release, WorkOrderAction.Cancel, WorkOrderAction.ChangePriority],
        WorkOrderStatus.Released =>
            [WorkOrderAction.Start, WorkOrderAction.Cancel, WorkOrderAction.ChangePriority, WorkOrderAction.PrintLabels],
        WorkOrderStatus.InProgress => HasOutput
            ? [WorkOrderAction.Hold, WorkOrderAction.Complete, WorkOrderAction.ChangePriority, WorkOrderAction.PrintLabels]
            : [WorkOrderAction.Hold, WorkOrderAction.Complete, WorkOrderAction.Cancel, WorkOrderAction.ChangePriority,
               WorkOrderAction.PrintLabels],
        WorkOrderStatus.OnHold => [WorkOrderAction.Resume, WorkOrderAction.ChangePriority],
        WorkOrderStatus.Completed => [WorkOrderAction.Close],
        _ => [],
    };

    // Rule R6: line, quantity, dates and the FAI flag change only while Draft. After Release the serials
    // and the snapshot depend on them; to change, cancel and create a new WO.
    public void UpdatePlan(int lineId, int plannedQty, DateTime plannedStart, DateTime plannedEnd, bool requireFai)
    {
        Ensure(WorkOrderAction.Edit, WorkOrderStatus.Draft);
        SetPlan(lineId, plannedQty, plannedStart, plannedEnd, requireFai);
    }

    // Rules R1-R3: the handler has already checked item/BOM/routing are approved and effective, and builds
    // the serials in the same SaveChanges. Here: freeze the recipe (values, not links) and open the gate.
    public void Release(int bomVersion, int routingVersion, IReadOnlyCollection<MaterialSnapshot> materials,
        IReadOnlyCollection<RoutingStepSnapshot> steps, DateTime utcNow)
    {
        Ensure(WorkOrderAction.Release, WorkOrderStatus.Draft);
        if (materials.Count == 0) throw new DomainException("The BOM has no material lines.");
        if (steps.Count == 0) throw new DomainException("The routing has no steps.");
        if (steps.Select(s => s.Seq).Distinct().Count() != steps.Count)
            throw new DomainException("The routing has duplicate step numbers.");
        var seqs = steps.Select(s => s.Seq).ToHashSet();
        var orphan = materials.FirstOrDefault(m => !seqs.Contains(m.OperationSeq));
        if (orphan is not null)
            throw new DomainException($"A material is consumed at step {orphan.OperationSeq}, which is not in the routing.");

        BomVersion = bomVersion;
        RoutingVersion = routingVersion;
        Materials.AddRange(materials.Select(m => new WorkOrderMaterial(m.ComponentItemId, m.OperationSeq, m.QtyPerUnit, PlannedQty)));
        Operations.AddRange(steps.OrderBy(s => s.Seq).Select(s => new WorkOrderOperation(s)));
        FaiStatus = RequireFai ? Planning.FaiStatus.Pending : Planning.FaiStatus.NotRequired;   // R16
        ReleasedAt = utcNow;
        Status = WorkOrderStatus.Released;
    }

    // Called by M3 on the first successful scan of the WO.
    public void Start(DateTime utcNow)
    {
        Ensure(WorkOrderAction.Start, WorkOrderStatus.Released);
        StartedAt = utcNow;
        Status = WorkOrderStatus.InProgress;
    }

    // M3: every accepted production scan makes sure the order runs. Two first boards scanned at once both read
    // "Released"; the second one finds the order already started by the first: nothing to do, not an error
    // (found by the step 10 load test). Any other status is still refused by Start.
    public void EnsureStarted(DateTime utcNow)
    {
        if (Status != WorkOrderStatus.InProgress) Start(utcNow);
    }

    // Rule R9: only a running order can be held; a Released order that should not run is cancelled instead.
    public void Hold(string reason)
    {
        Ensure(WorkOrderAction.Hold, WorkOrderStatus.InProgress);
        HoldReason = RequireReason(reason, "Hold");
        Status = WorkOrderStatus.OnHold;
    }

    public void Resume()
    {
        Ensure(WorkOrderAction.Resume, WorkOrderStatus.OnHold);
        HoldReason = null;   // the audit log keeps the old reason
        Status = WorkOrderStatus.InProgress;
    }

    // Rule R10: done when the last step has the planned good quantity; below that only by hand with a reason
    // ("3 pcs scrapped, customer accepts 197"), so the audit says why the order is short.
    public void Complete(string? manualReason, DateTime utcNow)
    {
        Ensure(WorkOrderAction.Complete, WorkOrderStatus.InProgress);
        if (QtyGoodLastStep < PlannedQty)
            CompleteReason = RequireReason(manualReason,
                $"Only {QtyGoodLastStep} of {PlannedQty} good at the last step; completing early");
        CompletedAt = utcNow;
        Status = WorkOrderStatus.Completed;
    }

    // Rule R10, automatic side: M3 calls this after counting a good unit at the last step. It passes the count it
    // just wrote (counters are updated in SQL, so the loaded Operations may be stale). Not an error when the
    // order is not running or not finished yet: most scans simply do not complete anything.
    public bool TryAutoComplete(int qtyGoodLastStep, DateTime utcNow)
    {
        if (Status != WorkOrderStatus.InProgress || qtyGoodLastStep < PlannedQty) return false;
        CompletedAt = utcNow;
        Status = WorkOrderStatus.Completed;
        return true;
    }

    // Rule R11: after reconciliation; nothing changes afterwards.
    public void Close()
    {
        Ensure(WorkOrderAction.Close, WorkOrderStatus.Completed);
        Status = WorkOrderStatus.Closed;
    }

    // Rule R7: no cancel once real units exist, or they become orphans. The handler scraps the unused
    // serials (R8) in the same transaction.
    public void Cancel(string reason)
    {
        Ensure(WorkOrderAction.Cancel, WorkOrderStatus.Draft, WorkOrderStatus.Released, WorkOrderStatus.InProgress);
        if (HasOutput) throw new DomainException("Cannot cancel a work order that already has output.");
        CancelReason = RequireReason(reason, "Cancel");
        Status = WorkOrderStatus.Cancelled;
    }

    // [v2] Rule R15: reordering the queue changes neither the snapshot nor the serials, so it stays open
    // until the order is finished.
    public void ChangePriority(int priority)
    {
        Ensure(WorkOrderAction.ChangePriority, WorkOrderStatus.Draft, WorkOrderStatus.Released,
            WorkOrderStatus.InProgress, WorkOrderStatus.OnHold);
        RequirePriority(priority);
        Priority = priority;
    }

    // Labels exist only for released serials, and not while the order is on hold (no new labels for a stopped
    // order) or finished. Changes nothing on the WO; the print log is separate.
    public void EnsureCanPrintLabels()
        => Ensure(WorkOrderAction.PrintLabels, WorkOrderStatus.Released, WorkOrderStatus.InProgress);

    // [v2] M3: the first serial scanned at the first step while FAI is pending becomes the sample.
    public void AssignFaiSerial(string serialNo)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serialNo);
        if (FaiStatus != Planning.FaiStatus.Pending) throw new DomainException("FAI is not pending for this work order.");
        FaiSerialNo ??= serialNo;   // later scans keep the first sample
    }

    // [v2] M7: QC judges the sample. Fail = WO stays Pending, the next serial becomes the new sample;
    // the details of each attempt live in FaiRecord (M7), not here.
    public void RecordFaiResult(bool passed)
    {
        if (FaiStatus != Planning.FaiStatus.Pending || FaiSerialNo is null)
            throw new DomainException("There is no FAI sample to judge.");
        if (passed) FaiStatus = Planning.FaiStatus.Passed;
        else FaiSerialNo = null;
    }

    private void SetPlan(int lineId, int plannedQty, DateTime plannedStart, DateTime plannedEnd, bool requireFai)
    {
        if (plannedQty <= 0) throw new DomainException("PlannedQty must be greater than 0.");
        if (plannedEnd <= plannedStart) throw new DomainException("PlannedEnd must be after PlannedStart.");

        LineId = lineId;
        PlannedQty = plannedQty;
        PlannedStart = plannedStart;
        PlannedEnd = plannedEnd;
        RequireFai = requireFai;
    }

    private void Ensure(WorkOrderAction action, params WorkOrderStatus[] allowed)
    {
        if (!allowed.Contains(Status))
            throw new DomainException($"Work order {WoNo} is {Status}; {action} is not allowed.");
    }

    private static string RequireReason(string? reason, string what)
    {
        if (string.IsNullOrWhiteSpace(reason)) throw new DomainException($"{what}: a reason is required.");
        if (reason.Length > ReasonMaxLength) throw new DomainException($"Reason must be at most {ReasonMaxLength} characters.");
        return reason.Trim();
    }

    private static void RequirePriority(int priority)
    {
        if (priority is < MinPriority or > MaxPriority)
            throw new DomainException($"Priority must be between {MinPriority} (most urgent) and {MaxPriority}.");
    }
}
