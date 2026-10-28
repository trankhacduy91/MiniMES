using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MiniMes.Contracts.Machines;
using MiniMes.EdgeGateway.Config;
using MQTTnet;
using MQTTnet.Protocol;

namespace MiniMes.EdgeGateway.Mqtt;

// The gateway's one MQTT connection (clientId minimes-gw-{id}; two gateways with the same id would kick each other
// off the broker). MQTTnet does not reconnect by itself: ExecuteAsync keeps the connection, 1-2-4..30 s backoff.
// Data messages reach it only through the outbox (ForwarderService); heartbeats directly (QoS 0, worthless if late).
// Step 6 adds the gateway's LWT.
public sealed class GatewayMqttClient(GatewayOptions options, TimeProvider clock, ILogger<GatewayMqttClient> log) : BackgroundService
{
    private readonly IMqttClient _client = new MqttClientFactory().CreateMqttClient();

    public bool IsConnected => _client.IsConnected;

    public Task<bool> PublishAsync(string topic, MachineMessage message, bool retain, MqttQualityOfServiceLevel qos, CancellationToken ct)
        => PublishRawAsync(topic, Serialize(message), retain, qos, ct);

    public static string Serialize(MachineMessage message) => JsonSerializer.Serialize(message, MachineJson.Options);

    // QoS 1: returns once the broker has acknowledged (PUBACK). false = not sent (not connected, or refused).
    // The payload is the exact text stored in the outbox: a resend is byte for byte the same message, same msgId.
    public async Task<bool> PublishRawAsync(string topic, string payload, bool retain, MqttQualityOfServiceLevel qos, CancellationToken ct)
    {
        if (!_client.IsConnected) return false;
        try
        {
            var result = await _client.PublishAsync(new MqttApplicationMessageBuilder()
                .WithTopic(topic)
                .WithPayload(payload)
                .WithQualityOfServiceLevel(qos)
                .WithRetainFlag(retain)
                .Build(), ct);
            return result.IsSuccess;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            log.LogWarning("MQTT publish to {Topic} failed: {Error}", topic, ex.Message);
            return false;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var mqtt = options.Mqtt;
        var connectOptions = new MqttClientOptionsBuilder()
            .WithTcpServer(mqtt.Host, mqtt.Port)
            .WithClientId(mqtt.ClientId)
            .WithKeepAlivePeriod(TimeSpan.FromSeconds(15))
            .WithTimeout(TimeSpan.FromSeconds(5))
            .Build();
        _client.DisconnectedAsync += e =>
        {
            if (e.ClientWasConnected && !stoppingToken.IsCancellationRequested)
                log.LogWarning("Gateway lost the broker {Host}:{Port} ({Reason})", mqtt.Host, mqtt.Port, e.Reason);
            return Task.CompletedTask;
        };

        var backoff = TimeSpan.FromSeconds(1);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!_client.IsConnected)
                {
                    await _client.ConnectAsync(connectOptions, stoppingToken);
                    backoff = TimeSpan.FromSeconds(1);
                    log.LogInformation("Gateway connected to the broker {Host}:{Port} as {ClientId}", mqtt.Host, mqtt.Port, mqtt.ClientId);
                }
                await Task.Delay(TimeSpan.FromSeconds(1), clock, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                log.LogWarning("Gateway cannot reach the broker {Host}:{Port} ({Error}), retry in {Backoff}s", mqtt.Host, mqtt.Port, ex.Message, backoff.TotalSeconds);
                try { await Task.Delay(backoff, clock, stoppingToken); }
                catch (OperationCanceledException) { break; }
                backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, 30));
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        if (_client.IsConnected)
            await _client.DisconnectAsync(new MqttClientDisconnectOptionsBuilder().Build(), cancellationToken);
        _client.Dispose();
    }
}
