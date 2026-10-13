using FluentValidation;
using MiniMes.Contracts.Factory;
using MiniMes.Domain.Factory;

namespace MiniMes.Application.Factory;

// Factory configuration (ISA-95: Site -> Area -> Line -> Station). Use cases are small, so each
// entity keeps its queries, commands, validators and handlers in one file (Sites.cs, Areas.cs, ...).

internal static class FactoryMapping
{
    public static SiteDto ToDto(this Site x) => new(x.Id, x.Code, x.Name, x.IsActive);
    public static AreaDto ToDto(this Area x) => new(x.Id, x.SiteId, x.Code, x.Name, x.IsActive);
    public static LineDto ToDto(this Line x) => new(x.Id, x.AreaId, x.Code, x.Name, x.IsActive);
    public static StationDto ToDto(this Station x) => new(x.Id, x.LineId, x.Code, x.Name, x.Type.ToString(), x.IsActive);
}

// Same rules as Domain FactoryRules, checked first so the caller gets 400 with a message per field.
internal static class FactoryValidation
{
    public static IRuleBuilderOptions<T, string> FactoryCode<T>(this IRuleBuilder<T, string> rule)
        => rule.NotEmpty().MaximumLength(FactoryRules.CodeMaxLength)
            .Matches(FactoryRules.CodePattern)
            .WithMessage("Code must contain only A-Z, 0-9 or '-' (no spaces): it is used in MQTT topics.");

    public static IRuleBuilderOptions<T, string> FactoryName<T>(this IRuleBuilder<T, string> rule)
        => rule.NotEmpty().MaximumLength(FactoryRules.NameMaxLength);

    public static IRuleBuilderOptions<T, string> StationTypeName<T>(this IRuleBuilder<T, string> rule)
        => rule.IsEnumName(typeof(StationType))
            .WithMessage($"Type must be one of: {string.Join(", ", StationTypes.All)}.");
}
