using FluentValidation;
using MiniMes.Domain.MasterData;
using MiniMes.Domain.MasterData.Equipment;

namespace MiniMes.Application.MasterData;

// Rule builders shared by the master data validators; they reuse the Domain constants so the 400
// (validator) and the 422 safety net (Domain) can never disagree.
internal static class MasterDataValidation
{
    public static IRuleBuilderOptions<T, string> MasterCode<T>(this IRuleBuilder<T, string> rule)
        => rule.NotEmpty().MaximumLength(MasterDataRules.CodeMaxLength)
            .Matches(MasterDataRules.CodePattern)
            .WithMessage("Code may contain only upper-case letters, digits, '-', '_' and '.'.");

    public static IRuleBuilderOptions<T, string> MasterName<T>(this IRuleBuilder<T, string> rule)
        => rule.NotEmpty().MaximumLength(MasterDataRules.NameMaxLength);

    // Spec M1 1.4 rule 10: the key becomes the {machine} level of an MQTT topic.
    public static IRuleBuilderOptions<T, string> MqttTopicKey<T>(this IRuleBuilder<T, string> rule)
        => rule.NotEmpty().MaximumLength(MasterDataRules.CodeMaxLength)
            .Must(key => key is null || !key.Any(c => c is '/' or '+' or '#' || char.IsWhiteSpace(c)))
            .WithMessage("MqttTopicKey must not contain '/', '+', '#' or spaces: they are MQTT separators and wildcards.");

    public static IRuleBuilderOptions<T, string> DataSourceName<T>(this IRuleBuilder<T, string> rule)
        => rule.IsEnumName(typeof(MachineDataSource))
            .WithMessage($"DataSource must be one of: {string.Join(", ", Enum.GetNames<MachineDataSource>())}.");
}
