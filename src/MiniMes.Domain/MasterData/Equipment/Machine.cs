using MiniMes.Domain.Common;

namespace MiniMes.Domain.MasterData.Equipment;

// A physical machine with state and counters (OEE is per machine, M5). Its line comes from the work center.
public class Machine : AuditableEntity, IAudited
{
    public int Id { get; private set; }
    public int WorkCenterId { get; private set; }
    public string Code { get; private set; }
    public string Name { get; private set; }
    public string Type { get; private set; }            // PickAndPlace, ReflowOven, Tester...
    public string MqttTopicKey { get; private set; }    // the {machine} level of minimes/{site}/{area}/{line}/{machine}/...
    public MachineDataSource DataSource { get; private set; }
    public bool IsActive { get; private set; } = true;

    private Machine() { Code = Name = Type = MqttTopicKey = string.Empty; }

    public Machine(int workCenterId, string code, string name, string type, string mqttTopicKey, MachineDataSource dataSource)
    {
        MasterDataRules.RequireCode(code, "Machine");
        MasterDataRules.RequireName(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        RequireTopicKey(mqttTopicKey);
        WorkCenterId = workCenterId;
        Code = code;
        Name = name;
        Type = type;
        MqttTopicKey = mqttTopicKey;
        DataSource = dataSource;
    }

    public void SetActive(bool isActive) => IsActive = isActive;

    // '/' is the MQTT level separator, '+' and '#' are wildcards: any of them breaks subscriptions (M4).
    public static void RequireTopicKey(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (key.Length > MasterDataRules.CodeMaxLength || key.Any(c => c is '/' or '+' or '#' || char.IsWhiteSpace(c)))
            throw new DomainException($"MqttTopicKey '{key}' must not contain '/', '+', '#' or spaces.");
    }
}
