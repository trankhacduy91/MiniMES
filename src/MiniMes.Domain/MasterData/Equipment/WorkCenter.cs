using MiniMes.Domain.Common;

namespace MiniMes.Domain.MasterData.Equipment;

// A capability group on a line ("where SMT is done"). Routing steps point here, not at a machine,
// so a broken machine can be swapped without touching the routing.
public class WorkCenter : AuditableEntity, IAudited
{
    public int Id { get; private set; }
    public int LineId { get; private set; }
    public string Code { get; private set; }
    public string Name { get; private set; }
    public string Type { get; private set; }   // same vocabulary as RoutingStep.OperationCode: SMT, ASSY, TEST, PACK
    public bool IsActive { get; private set; } = true;

    private WorkCenter() { Code = Name = Type = string.Empty; }

    public WorkCenter(int lineId, string code, string name, string type)
    {
        MasterDataRules.RequireCode(code, "Work center");
        MasterDataRules.RequireName(name);
        MasterDataRules.RequireCode(type, "Work center type");
        LineId = lineId;
        Code = code;
        Name = name;
        Type = type;
    }

    // Code and LineId are fixed: routings, machines and stations point at this work center.
    public void Update(string name, string type)
    {
        MasterDataRules.RequireName(name);
        MasterDataRules.RequireCode(type, "Work center type");
        Name = name;
        Type = type;
    }

    public void SetActive(bool isActive) => IsActive = isActive;
}
