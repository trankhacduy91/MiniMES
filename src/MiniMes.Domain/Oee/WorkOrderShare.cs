namespace MiniMes.Domain.Oee;

// Scans of one work order at the machine's station in the window, with that WO's ideal cycle time (snapshot).
public readonly record struct WorkOrderScans(int WorkOrderId, double IdealCycleSec, long Scans);

// Spec M5 rule 3: Ideal = Σ count_wo × ict_wo, but a machine counter does not know which WO a board belongs to;
// the serial scans do. The machine's total is shared out in proportion to each WO's scans (decision of step 4).
public static class WorkOrderShare
{
    public static IReadOnlyList<ProductionSegment> Split(long machineTotal, IReadOnlyList<WorkOrderScans> workOrders)
    {
        long scans = workOrders.Sum(w => w.Scans);
        if (workOrders.Count == 0 || scans == 0)
            return [new ProductionSegment(workOrders.Count == 0 ? 0 : workOrders[0].IdealCycleSec, machineTotal)];

        // Largest remainder: floor every share, then give the boards left over to the largest fractions, so the
        // shares always add up to the machine total exactly.
        var shares = workOrders
            .Select((w, i) => (Index: i, Exact: (double)machineTotal * w.Scans / scans))
            .Select(x => (x.Index, Count: (long)Math.Floor(x.Exact), Fraction: x.Exact - Math.Floor(x.Exact)))
            .ToList();
        var left = machineTotal - shares.Sum(s => s.Count);
        var bonus = shares.OrderByDescending(s => s.Fraction).ThenBy(s => s.Index).Take((int)left).Select(s => s.Index).ToHashSet();

        return shares.Select(s => new ProductionSegment(workOrders[s.Index].IdealCycleSec, s.Count + (bonus.Contains(s.Index) ? 1 : 0)))
            .ToList();
    }
}
