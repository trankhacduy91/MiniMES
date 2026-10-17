using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MiniMes.Application.Machines;
using MiniMes.Contracts.Machines;
using MQTTnet;
using MQTTnet.Protocol;

namespace MiniMes.Infrastructure.Machines;

// Spec M4 2.2 / 2.4: the MES side of MQTT. Step 3 = receive, check, drop duplicates, log; nothing is stored yet
// (the hot path and the batch writer come in step 4).
// MQTTnet does not reconnect by itself: ExecuteAsync keeps the connection with a 1-2-4..30 s backoff, and
// subscribes again on every connect instead of trusting the broker to remember.
public sealed class MqttIngestService(
    IOptions<MqttIngestOptions> options,
    MachineDirectory machines,
    RecentIdCache seen,
    TimeProvider clock,
    ILogger<MqttIngestService> log) : BackgroundService
{
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    private readonly MqttIngestOptions _opt = options.Value;
    private readonly IMqttClient _client = new MqttClientFactory().CreateMqttClient();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var mqttOptions = new MqttClientOptionsBuilder()
            .WithTcpServer(_opt.Host, _opt.Port)
            .WithClientId(_opt.ClientId)
            .WithKeepAlivePeriod(TimeSpan.FromSeconds(15))
            .WithCleanSession(false)           // the broker queues our QoS 1 messages while we are briefly away
            .Build();

        // MQTTnet calls this handler one message at a time: messages of a machine are handled in arrival order.
        _client.ApplicationMessageReceivedAsync += e => OnMessageAsync(e, stoppingToken);
        _client.DisconnectedAsync += e =>
        {
            if (e.ClientWasConnected) log.LogWarning("MQTT ingest lost the broker ({Reason})", e.Reason);
            return Task.CompletedTask;
        };

        var backoff = TimeSpan.FromSeconds(1);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!_client.IsConnected)
                {
                    await _client.ConnectAsync(mqttOptions, stoppingToken);
                    await SubscribeAsync(stoppingToken);
                    backoff = TimeSpan.FromSeconds(1);
                    log.LogInformation("MQTT ingest connected to {Host}:{Port}", _opt.Host, _opt.Port);
                }
                await Task.Delay(TimeSpan.FromSeconds(2), clock, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                // An exception out of ExecuteAsync stops the whole host (the API): never let one escape.
                log.LogWarning("MQTT ingest cannot reach {Host}:{Port} ({Error}), retry in {Backoff}", _opt.Host, _opt.Port, ex.Message, backoff);
                try { await Task.Delay(backoff, clock, stoppingToken); }
                catch (OperationCanceledException) { break; }
                backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, MaxBackoff.TotalSeconds));
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

    private Task SubscribeAsync(CancellationToken ct)
        => _client.SubscribeAsync(new MqttClientSubscribeOptionsBuilder()
            .WithTopicFilter(f => f.WithTopic(MachineTopic.AllMachineKinds).WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce))
            .WithTopicFilter(f => f.WithTopic(MachineTopic.AllProcessTags).WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce))
            .Build(), ct);

    // Fast and never throwing: parse, check, drop. A slow or failing handler would block the receive loop and the
    // keep-alive; an exception would only be swallowed by MQTTnet and the problem would vanish silently.
    private async Task OnMessageAsync(MqttApplicationMessageReceivedEventArgs e, CancellationToken ct)
    {
        var topic = e.ApplicationMessage.Topic;
        try
        {
            var read = MachineMessageReader.Read(topic, e.ApplicationMessage.ConvertPayloadToString());
            if (!read.IsAccepted)
            {
                log.LogWarning("MQTT dropped {Topic}: {Reason}", topic, read.RejectReason);
                return;
            }

            var input = read.Input!;
            var machine = await machines.ResolveAsync(input.Topic, ct);
            if (machine is null)
            {
                log.LogWarning("MQTT dropped {Topic}: no active machine with this site/area/line/MqttTopicKey", topic);
                return;
            }

            if (input.Status is not null)        // online/offline: used by the watchdog in step 5
            {
                log.LogDebug("MQTT status {Machine} {Status}", machine.Code, input.Status);
                return;
            }

            var msg = input.Message!;
            if (!seen.TryAdd(msg.MsgId))
            {
                log.LogDebug("MQTT duplicate {Kind} {Machine} {MsgId} dropped", input.Topic.Kind, machine.Code, msg.MsgId);
                return;
            }

            log.LogDebug("MQTT processed {Kind} {Machine} {MsgId} state={State} counter={Counter} alarm={AlarmCode} tag={Tag} value={Value}",
                input.Topic.Kind, machine.Code, msg.MsgId, msg.State, msg.Counter, msg.AlarmCode, msg.Tag, msg.Value);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            log.LogError(ex, "MQTT message on {Topic} failed", topic);
        }
    }
}
