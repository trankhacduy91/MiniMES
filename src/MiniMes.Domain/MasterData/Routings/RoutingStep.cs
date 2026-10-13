using MiniMes.Domain.Common;

namespace MiniMes.Domain.MasterData.Routings;

public class RoutingStep : IAudited
{
    public const int SopUrlMaxLength = 500;

    public int Id { get; private set; }
    public int RoutingId { get; private set; }
    public int Seq { get; private set; }                    // 10, 20, 30... leaves room to insert 25 later
    public string OperationCode { get; private set; }       // SMT, ASSY, TEST, PACK
    public int WorkCenterId { get; private set; }           // where, not which machine: machines can be swapped
    public decimal IdealCycleTimeSec { get; private set; }  // per 1 unit; OEE Performance (M5) divides by it
    public bool IsInspection { get; private set; }          // has an OK/NG result and defect codes
    public bool IsMandatory { get; private set; }
    public string? SopUrl { get; private set; }             // [v2] work instruction opened from the kiosk (M3)

    private RoutingStep() { OperationCode = string.Empty; }

    internal RoutingStep(int seq, string operationCode, int workCenterId, decimal idealCycleTimeSec,
        bool isInspection, bool isMandatory, string? sopUrl)
    {
        if (seq <= 0) throw new DomainException("Seq must be greater than 0.");
        MasterDataRules.RequireCode(operationCode, "Operation");
        if (idealCycleTimeSec <= 0) throw new DomainException("IdealCycleTimeSec must be greater than 0.");
        if (sopUrl is { Length: > SopUrlMaxLength })
            throw new DomainException($"SopUrl must be at most {SopUrlMaxLength} characters.");

        Seq = seq;
        OperationCode = operationCode;
        WorkCenterId = workCenterId;
        IdealCycleTimeSec = idealCycleTimeSec;
        IsInspection = isInspection;
        IsMandatory = isMandatory;
        SopUrl = string.IsNullOrWhiteSpace(sopUrl) ? null : sopUrl;
    }
}
