namespace MiniMes.Infrastructure.Machines;

// "Mqtt" section. Enabled = false in Testing: the tests that need a broker start their own (Mosquitto container, step 9).
public sealed class MqttIngestOptions
{
    public const string SectionName = "Mqtt";

    public bool Enabled { get; set; } = true;
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 1883;
    public string ClientId { get; set; } = "minimes-ingest";   // fixed: the broker keeps our session by this id
}
