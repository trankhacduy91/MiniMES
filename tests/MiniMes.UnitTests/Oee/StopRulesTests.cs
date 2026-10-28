using MiniMes.Domain.Machines;
using MiniMes.Domain.Oee;

namespace MiniMes.UnitTests.Oee;

// Spec M5 1.2.4: the table of states, plus the reason override and the "Idle with work" decision of step 4.
public class StopRulesTests
{
    [Theory]
    [InlineData(MachineState.Running, null, true, StopTreatment.Run)]
    [InlineData(MachineState.PlannedStop, null, true, StopTreatment.NotPlanned)]
    [InlineData(MachineState.PlannedStop, true, true, StopTreatment.NotPlanned)]   // planned stop is never a loss
    [InlineData(MachineState.Down, null, true, StopTreatment.Downtime)]            // to classify, still a loss (rule 7)
    [InlineData(MachineState.Down, true, true, StopTreatment.Downtime)]            // FDR-JAM
    [InlineData(MachineState.Down, false, true, StopTreatment.NotPlanned)]         // reclassified as PM
    [InlineData(MachineState.Setup, null, true, StopTreatment.Downtime)]           // default: penalised
    [InlineData(MachineState.Setup, false, true, StopTreatment.NotPlanned)]
    [InlineData(MachineState.Idle, null, true, StopTreatment.Downtime)]            // waits while a WO runs
    [InlineData(MachineState.Idle, null, false, StopTreatment.NotPlanned)]         // nothing planned for it
    [InlineData(MachineState.Idle, true, false, StopTreatment.Downtime)]           // a reason wins
    [InlineData(MachineState.Offline, null, false, StopTreatment.Downtime)]        // no data: loss, to confirm
    public void Each_state_goes_one_way(MachineState state, bool? counts, bool hasPlan, StopTreatment expected)
        => Assert.Equal(expected, StopRules.Treat(state, counts, hasPlan));
}

public class WorkOrderShareTests
{
    // Hand calc (c): 600 PCB-A and 200 PCB-B scanned, machine counted 800.
    [Fact]
    public void Machine_total_is_shared_by_scans()
    {
        var segments = WorkOrderShare.Split(800, [new(1, 20, 600), new(2, 30, 200)]);

        Assert.Equal([new ProductionSegment(20, 600), new ProductionSegment(30, 200)], segments);
    }

    // 10 boards over three equal WOs: 3.33 each; the leftover board goes to the first (largest remainder, tie by order).
    [Fact]
    public void Shares_always_add_up_to_the_machine_total()
    {
        var segments = WorkOrderShare.Split(10, [new(1, 20, 1), new(2, 30, 1), new(3, 40, 1)]);

        Assert.Equal([4L, 3L, 3L], segments.Select(s => s.TotalCount));
    }

    [Fact]
    public void Machine_counted_more_than_was_scanned_keeps_its_total()
        => Assert.Equal(1000, WorkOrderShare.Split(1000, [new(1, 20, 985)]).Sum(s => s.TotalCount));

    [Fact]
    public void No_scan_and_no_work_order_gives_an_unknown_cycle_time()
        => Assert.Equal([new ProductionSegment(0, 50)], WorkOrderShare.Split(50, []));
}
