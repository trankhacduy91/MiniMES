using MiniMes.Domain.Common;

namespace MiniMes.Domain.MasterData.Boms;

public class BomLine : IAudited
{
    public int Id { get; private set; }
    public int BomId { get; private set; }
    public int ComponentItemId { get; private set; }
    public decimal QtyPer { get; private set; }       // per 1 unit of the parent, in the component's UoM
    public decimal ScrapRate { get; private set; }    // fraction: 2% = 0.02; need = qty x QtyPer x (1 + ScrapRate)
    public int OperationSeq { get; private set; }     // routing step that consumes it (M6 material consumption)

    private BomLine() { }

    internal BomLine(int componentItemId, decimal qtyPer, decimal scrapRate, int operationSeq)
    {
        if (qtyPer <= 0) throw new DomainException("QtyPer must be greater than 0.");
        if (scrapRate is < 0 or >= 1)
            throw new DomainException("ScrapRate is a fraction: 0 <= ScrapRate < 1 (2% = 0.02).");
        if (operationSeq <= 0) throw new DomainException("OperationSeq must be greater than 0.");

        ComponentItemId = componentItemId;
        QtyPer = qtyPer;
        ScrapRate = scrapRate;
        OperationSeq = operationSeq;
    }
}
