using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MiniMes.Application.Machines;
using MiniMes.Application.Realtime;
using MiniMes.Contracts.Machines;
using MQTTnet;
using MQTTnet.Protocol;

namespace MiniMes.Infrastructure.Machines;

// Spec M4 2.2 / 2.4: the MES side of MQTT. Receive, check, drop duplicates (step 3), then the hot path: the runtime
// store turns the message into rows in RAM and the rows go to the channel of the batch writer (step 4).
// MQTTnet does not reconnect by itself: ExecuteAsync keeps the connection with a 1-2-4..30 s backoff, and
// subscribes again on every connect instead of trusting the broker to remember.
public sealed class MqttIngestService(
    IOptions<MqttIngestOptions> options,
    MachineDirectory machines,
    RecentIdCache seen,
    MachineRuntimeStore store,
    Channel<IngestItem> channel,
    IServiceScopeFactory scopes,
    MqttConnectionState connection,
    MachineStateService states,
    GatewayStatusStore gateways,
    IRealtimeNotifier notifier,
    TimeProvider clock,
    ILogger<MqttIngestService> log) : BackgroundService
{
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    private readonly MqttIngestOptions _opt = options.Value;
    private readonly IMqttClient _client = new MqttClientFactory().CreateMqttClient();
    private readonly ConcurrentDictionary<int, bool> _skewed = new();   // per machine: is its clock off right now

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!await LoadRuntimeAsync(stoppingToken)) return;

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
            connection.Disconnected();   // the watchdog stops judging machines until we have been back for 30 s
            if (e.ClientWasConnected && !stoppingToken.IsCancellationRequested) log.LogWarning("MQTT ingest lost the broker ({Reason})", e.Reason);
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
                    connection.Connected(clock.GetUtcNow().UtcDateTime);
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

    // Before the first message: counters and open segments come back from the DB (retried while the DB is down).
    private async Task<bool> LoadRuntimeAsync(CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var machines = await MachineRuntimeLoader.LoadAsync(scopes, store, ct);
                log.LogInformation("MQTT ingest: runtime of {Count} machines loaded from the database", machines);
                return true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return false; }
            catch (Exception ex)
            {
                var delay = TimeSpan.FromSeconds(Math.Min(attempt * 2, 30));
                log.LogError("MQTT ingest: loading the machine runtime failed ({Error}), retry in {Delay}", ex.Message, delay);
                try { await Task.Delay(delay, clock, ct); } catch (OperationCanceledException) { return false; }
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
            // [M4b] The only change of M4b on the MES side: the edge gateway's own status ("_sys" is not an area,
            // so this goes before the machine lookup, which would drop it as an unknown machine).
            if (GatewayTopic.TryParseStatus(topic, out var site, out var gatewayId))
            {
                await OnGatewayStatusAsync(site, gatewayId, e.ApplicationMessage.ConvertPayloadToString(), ct);
                return;
            }

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

            if (input.Status == MachineMessageReader.Online)
            {
                await states.MarkOnlineAsync(machine.Id, ct);
                return;
            }
            if (input.Status == MachineMessageReader.Offline)
            {
                // Live "offline" = the broker published the machine's LWT (or the gateway lost the PLC). Only a report:
                // the watchdog confirms it 3 s later if our connection survived (a broker shutting down sends every
                // LWT, then drops us too). A retained one is replayed on every subscribe and may be old: a machine that
                // is alive sends its heartbeat within 5 s, a dead one is caught by the watchdog after the grace period.
                if (e.ApplicationMessage.Retain)
                {
                    log.LogDebug("MQTT retained offline of {Machine} ignored, the watchdog decides", machine.Code);
                }
                else
                {
                    store.ReportOffline(machine.Id, clock.GetUtcNow().UtcDateTime);
                    log.LogInformation("MQTT offline reported for {Machine} (LWT), confirming", machine.Code);
                }
                return;
            }

            var msg = input.Message!;
            if (!seen.TryAdd(msg.MsgId))
            {
                log.LogDebug("MQTT duplicate {Kind} {Machine} {MsgId} dropped", input.Topic.Kind, machine.Code, msg.MsgId);
                return;
            }

            IngestResult result;
            if (input.Topic.Kind == MachineKinds.Process)
            {
                // [v2] Only tags in master data (MachineTag): an unknown tag is a gateway configuration to fix,
                // not data to keep.
                var tag = await machines.ResolveTagAsync(machine.Id, input.Topic.Tag!, ct);
                if (tag is null)
                {
                    log.LogWarning("MQTT dropped {Topic}: tag {Tag} is not an active MachineTag of {Machine}", topic, input.Topic.Tag, machine.Code);
                    return;
                }
                result = store.ApplyProcess(tag, msg, clock.GetUtcNow().UtcDateTime);
                if (result.Note is not null) log.LogWarning("MQTT {Machine}/{Tag}: {Note}", machine.Code, tag.TagName, result.Note);
            }
            else
            {
                result = store.Apply(machine.Id, input.Topic.Kind, msg, clock.GetUtcNow().UtcDateTime);
            }
            LogClockSkew(machine, result.ClockSkewed, msg);
            log.LogDebug("MQTT processed {Kind} {Machine} {MsgId} state={State} counter={Counter} delta={Delta} stateChange={StateChange} value={Value} quality={Quality} {Note}",
                input.Topic.Kind, machine.Code, msg.MsgId, msg.State, msg.Counter, result.Item?.Event?.Delta,
                result.Item?.StateChange?.State, msg.Value, result.Item?.ProcessValue?.Quality, result.Note);
            // State change: to the dashboards now, before the row reaches the DB (hot path first).
            await states.PushStateChangeAsync(result.Item?.StateChange, ct);
            // Bounded channel: when the DB is slow this waits, and so does the MQTT client (backpressure).
            if (result.Item is not null) await channel.Writer.WriteAsync(result.Item, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            log.LogError(ex, "MQTT message on {Topic} failed", topic);
        }
    }

    // Once when a machine's clock goes off (> 60 s) and once when it is back, not on every message.
    private async Task OnGatewayStatusAsync(string site, string gatewayId, string payload, CancellationToken ct)
    {
        var previous = gateways.Get(gatewayId);
        var (status, error) = GatewayStatusStore.Read(site, gatewayId, payload, clock.GetUtcNow().UtcDateTime, previous);
        if (status is null)
        {
            log.LogWarning("MQTT dropped status of gateway {Gateway}: {Reason}", gatewayId, error);
            return;
        }
        gateways.Set(status);
        if (status.State != previous?.State)
            log.Log(status.State == GatewayStates.Offline ? LogLevel.Warning : LogLevel.Information,
                "Gateway {Gateway} is {State} (queue {QueueLength}, dropped {DroppedCount})", gatewayId, status.State, status.QueueLength, status.DroppedCount);
        await notifier.GatewayStatusChangedAsync(status, ct);
    }

    private void LogClockSkew(MachineRef machine, bool skewed, MachineMessage msg)
    {
        if (_skewed.GetValueOrDefault(machine.Id) == skewed) return;
        _skewed[machine.Id] = skewed;
        if (skewed)
            log.LogWarning("Clock of {Machine} is off ({MachineTs:O}), the received time is used instead", machine.Code, msg.Ts);
        else
            log.LogInformation("Clock of {Machine} is back in range", machine.Code);
    }
}
