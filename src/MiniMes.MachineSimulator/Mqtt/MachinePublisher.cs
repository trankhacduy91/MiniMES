using Microsoft.Extensions.Logging;
using MiniMes.Contracts.Machines;
using MiniMes.MachineSimulator.Config;
using MQTTnet;
using MQTTnet.Protocol;

namespace MiniMes.MachineSimulator.Mqtt;

// One MQTT client per machine (ClientId sim-{machine}), so each machine has its own LWT and can drop alone (spec 2.8).
// MQTTnet does not reconnect by itself: RunAsync keeps the connection with a 1-2-4-8-10 s backoff. Capped at 10 s
// (the ingest waits up to 30 s): after a broker restart the MES gives each machine 30 s to be heard again before it
// counts as Offline, so a machine must not sleep longer than that between two attempts.
// Publishing never throws: while the broker is away it returns false and the machine keeps counting,
// so the next count message after the reconnect carries the new total (spec 2.3).
public sealed class MachinePublisher : IDisposable
{
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(10);

    private readonly IMqttClient _client = new MqttClientFactory().CreateMqttClient();
    private readonly MqttClientOptions _options;
    private readonly string _machine;
    private readonly string _statusTopic;
    private readonly TimeProvider _clock;
    private readonly ILogger _log;

    public MachinePublisher(MqttOptions mqtt, string machine, string statusTopic, TimeProvider clock, ILogger log)
    {
        _machine = machine;
        _statusTopic = statusTopic;
        _clock = clock;
        _log = log;
        _options = new MqttClientOptionsBuilder()
            .WithTcpServer(mqtt.Host, mqtt.Port)
            .WithClientId($"sim-{machine}")
            .WithKeepAlivePeriod(TimeSpan.FromSeconds(15))
            .WithTimeout(TimeSpan.FromSeconds(5))
            // The broker publishes this for us if the TCP connection dies without a DISCONNECT (killed process, cable).
            .WithWillTopic(statusTopic)
            .WithWillPayload("offline")
            .WithWillQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .WithWillRetain()
            .Build();

        _client.DisconnectedAsync += e =>
        {
            if (e.ClientWasConnected)
                _log.LogWarning("{Machine} lost the broker ({Reason}), still counting", _machine, e.Reason);
            return Task.CompletedTask;
        };
    }

    public bool IsConnected => _client.IsConnected;

    // Called after every (re)connect: the machine republishes "online" and its retained state.
    public Func<CancellationToken, Task>? Connected { get; set; }

    public async Task RunAsync(CancellationToken ct)
    {
        var backoff = TimeSpan.FromSeconds(1);
        while (!ct.IsCancellationRequested)
        {
            if (!_client.IsConnected)
            {
                try
                {
                    await _client.ConnectAsync(_options, ct);
                    backoff = TimeSpan.FromSeconds(1);
                    _log.LogInformation("{Machine} connected to the broker", _machine);
                    if (Connected is not null) await Connected(ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log.LogWarning("{Machine} cannot reach the broker ({Error}), retry in {Backoff}", _machine, ex.Message, backoff);
                    await Task.Delay(backoff, _clock, ct);
                    backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, MaxBackoff.TotalSeconds));
                    continue;
                }
            }
            await Task.Delay(TimeSpan.FromSeconds(1), _clock, ct);
        }
    }

    public Task<bool> PublishAsync(string topic, MachineMessage message, MqttQualityOfServiceLevel qos, bool retain, CancellationToken ct)
        => PublishRawAsync(topic, System.Text.Json.JsonSerializer.Serialize(message, MachineJson.Options), qos, retain, ct);

    public Task<bool> PublishStatusAsync(string status, CancellationToken ct)
        => PublishRawAsync(_statusTopic, status, MqttQualityOfServiceLevel.AtLeastOnce, retain: true, ct);

    // Ctrl+C is a machine switched off properly: say "offline" ourselves (a clean DISCONNECT does not fire the LWT).
    public async Task StopAsync(CancellationToken ct)
    {
        if (!_client.IsConnected) return;
        await PublishStatusAsync("offline", ct);
        try { await _client.DisconnectAsync(new MqttClientDisconnectOptionsBuilder().Build(), ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogDebug(ex, "{Machine} disconnect failed", _machine); }
    }

    private async Task<bool> PublishRawAsync(string topic, string payload, MqttQualityOfServiceLevel qos, bool retain, CancellationToken ct)
    {
        if (!_client.IsConnected) return false;
        try
        {
            var message = new MqttApplicationMessageBuilder()
                .WithTopic(topic)
                .WithPayload(payload)
                .WithQualityOfServiceLevel(qos)
                .WithRetainFlag(retain)
                .Build();
            var result = await _client.PublishAsync(message, ct);
            return result.IsSuccess;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The connection dropped between the check and the send: the reconnect loop logs it, the caller retries.
            _log.LogDebug(ex, "{Machine} publish to {Topic} failed", _machine, topic);
            return false;
        }
    }

    public void Dispose() => _client.Dispose();
}
