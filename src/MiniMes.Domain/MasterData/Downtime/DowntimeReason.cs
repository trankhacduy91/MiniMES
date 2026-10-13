using System.Diagnostics.CodeAnalysis;
using MiniMes.Domain.Common;

namespace MiniMes.Domain.MasterData.Downtime;

public enum DowntimeCategory
{
    Planned,    // maintenance, no plan, changeover
    Unplanned,  // breakdown, waiting for material, feeder jam
}

public class DowntimeReason : AuditableEntity, IAudited
{
    public int Id { get; private set; }
    public string Code { get; private set; }
    public string Name { get; private set; }
    public DowntimeCategory Category { get; private set; }      // business reporting
    public bool CountsAgainstAvailability { get; private set; } // OEE switch (M5): false = removed from planned time
    public bool IsActive { get; private set; } = true;

    private DowntimeReason() { Code = Name = string.Empty; }

    public DowntimeReason(string code, string name, DowntimeCategory category, bool countsAgainstAvailability)
    {
        MasterDataRules.RequireCode(code, "Downtime reason");
        Code = code;
        Update(name, category, countsAgainstAvailability);
    }

    // Changing the OEE switch affects reports from now on; history keeps the stops it already has (M5).
    [MemberNotNull(nameof(Name))]
    public void Update(string name, DowntimeCategory category, bool countsAgainstAvailability)
    {
        MasterDataRules.RequireName(name);
        // An unplanned stop can never be "excused" from Availability (spec M1 1.4 rule 12).
        if (category == DowntimeCategory.Unplanned && !countsAgainstAvailability)
            throw new DomainException("Unplanned downtime always counts against availability.");
        Name = name;
        Category = category;
        CountsAgainstAvailability = countsAgainstAvailability;
    }

    public void SetActive(bool isActive) => IsActive = isActive;
}
