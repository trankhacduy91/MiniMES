using MiniMes.Domain.Common;

namespace MiniMes.Domain.Planning;

// One material line of a released WO, copied from the BOM explosion (values, not a link to the BOM),
// so a later BOM version never changes a running order (spec M2 2.3).
public class WorkOrderMaterial : IAudited
{
    public int Id { get; private set; }
    public int WorkOrderId { get; private set; }
    public int ComponentItemId { get; private set; }
    public int OperationSeq { get; private set; }     // step that consumes it (M6)
    public decimal QtyPerUnit { get; private set; }   // per 1 unit of the WO item, all BOM levels and scrap included
    public decimal RequiredQty { get; private set; }  // PlannedQty x QtyPerUnit, exact; rounded only for display (ADR-005)
    public decimal IssuedQty { get; private set; }    // M6: issued from the warehouse
    public decimal ConsumedQty { get; private set; }  // M6: used by scans

    private WorkOrderMaterial() { }

    internal WorkOrderMaterial(int componentItemId, int operationSeq, decimal qtyPerUnit, int plannedQty)
    {
        if (qtyPerUnit <= 0) throw new DomainException("Material quantity per unit must be greater than 0.");
        if (operationSeq <= 0) throw new DomainException("OperationSeq must be greater than 0.");

        ComponentItemId = componentItemId;
        OperationSeq = operationSeq;
        QtyPerUnit = qtyPerUnit;
        RequiredQty = plannedQty * qtyPerUnit;
    }
}

// Input of WorkOrder.Release: one leaf material of the exploded BOM for 1 unit of the WO item.
public sealed record MaterialSnapshot(int ComponentItemId, int OperationSeq, decimal QtyPerUnit);
