using System.Diagnostics.CodeAnalysis;
using MiniMes.Domain.Common;

namespace MiniMes.Domain.MasterData.DefectCodes;

// Controlled vocabulary for NG results: Pareto (M5/M7) is GROUP BY Code, so free text is not allowed.
public class DefectCode : AuditableEntity, IAudited
{
    public const int CategoryMaxLength = 50;

    public int Id { get; private set; }
    public string Code { get; private set; }
    public string Name { get; private set; }
    public string Category { get; private set; }   // open list: Solder, Component, Functional, Cosmetic...
    public bool IsActive { get; private set; } = true;

    private DefectCode() { Code = Name = Category = string.Empty; }

    public DefectCode(string code, string name, string category)
    {
        MasterDataRules.RequireCode(code, "Defect");
        Code = code;
        Update(name, category);
    }

    // Code is fixed: NG records and Pareto reports group by it.
    [MemberNotNull(nameof(Name), nameof(Category))]
    public void Update(string name, string category)
    {
        MasterDataRules.RequireName(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(category);
        if (category.Length > CategoryMaxLength)
            throw new DomainException($"Category must be at most {CategoryMaxLength} characters.");
        Name = name;
        Category = category;
    }

    public void SetActive(bool isActive) => IsActive = isActive;
}
