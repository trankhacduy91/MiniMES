using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using MiniMes.Contracts.Machines;
using MiniMes.EdgeGateway.Config;
using Opc.Ua;
using Opc.Ua.Client;

namespace MiniMes.EdgeGateway.Drivers.OpcUa;

// Spec M4b 2.3: one session per server, one subscription holding every tag of the machine. The server samples,
// filters (deadband) and pushes; this class only turns notifications into RawValues.
// Any failure (server stopped, network, keep-alive missed) = drop the session, report "disconnected", wait (backoff),
// build a new session and subscription. Simple and easy to follow; SessionReconnectHandler (keeps the subscription
// over a short outage) is the optimisation the spec suggests trying later.
public sealed class OpcUaDriver(DeviceOptions device, OpcUaClientConfiguration client, ChannelWriter<DeviceEvent> events,
    TimeProvider clock, ILogger<OpcUaDriver> log) : IDeviceDriver
{
    public string Machine => device.Machine;
    public string Protocol => Protocols.OpcUa;

    public async Task RunAsync(CancellationToken ct)
    {
        var backoff = Backoff.Exponential(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30));
        var connected = false;
        while (!ct.IsCancellationRequested)
        {
            string reason;
            try
            {
                reason = await RunSessionAsync(() => { connected = true; backoff.Reset(); }, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                reason = ex is ServiceResultException sre ? StatusCodes.GetBrowseName(sre.StatusCode) + ": " + sre.Message : ex.Message;
            }

            if (connected)
            {
                connected = false;
                events.TryWrite(new ConnectionChanged(Machine, Connected: false, reason));
            }
            var delay = backoff.Next();
            log.LogWarning("OPC UA {Machine} at {Endpoint}: {Reason}; retry in {Delay}s", Machine, device.Endpoint, reason, delay.TotalSeconds);
            try { await Task.Delay(delay, clock, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    // Returns why the session ended; throws when it could not be built.
    private async Task<string> RunSessionAsync(Action onConnected, CancellationToken ct)
    {
        var config = await client.GetAsync(ct);
        // Security None: dev only (spec section 4). Production selects a SignAndEncrypt endpoint (useSecurity: true).
        var endpoint = await CoreClientUtils.SelectEndpointAsync(config, device.Endpoint, useSecurity: false, client.Telemetry, ct);
        using var session = await new DefaultSessionFactory(client.Telemetry).CreateAsync(config,
            new ConfiguredEndpoint(null, endpoint, EndpointConfiguration.Create(config)),
            updateBeforeConnect: false, sessionName: $"minimes-gw-{Machine}", sessionTimeout: 60_000,
            identity: new UserIdentity(new AnonymousIdentityToken()), preferredLocales: null, ct);

        // Keep-alive = the client asks "are you there?" every KeepAliveInterval when nothing else is flowing.
        // A bad status is the only sign of a server that vanished without closing the TCP connection.
        var broken = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.KeepAliveInterval = client.KeepAliveMs;
        session.KeepAlive += (_, e) =>
        {
            if (ServiceResult.IsBad(e.Status)) broken.TrySetResult($"keep-alive failed ({StatusCodes.GetBrowseName(e.Status!.StatusCode.Code)})");
        };

        var subscription = new Subscription(session.DefaultSubscription)
        {
            DisplayName = $"minimes-gw-{Machine}",
            PublishingInterval = device.PublishingMs,
            PublishingEnabled = true,
            // The server keeps the subscription (and its queued values) as long as the session can be resumed.
            MinLifetimeInterval = 60_000,
        };
        foreach (var tag in device.Tags)
            if (MonitoredItemFor(tag, session) is { } item) subscription.AddItem(item);
        session.AddSubscription(subscription);   // before CreateAsync: the other way round fails (spec 2.3, trap 2)
        await subscription.CreateAsync(ct);

        // A wrong NodeId does not fail the subscription: only that item is Bad. Say it loudly, it is a config error.
        foreach (var item in subscription.MonitoredItems.Where(i => ServiceResult.IsBad(i.Status.Error)))
            log.LogError("OPC UA {Machine}: tag {Tag} ({NodeId}) refused by the server: {Status}",
                Machine, item.DisplayName, item.StartNodeId, StatusCodes.GetBrowseName(item.Status.Error!.StatusCode.Code));

        log.LogInformation("OPC UA {Machine} connected to {Endpoint}, {Items} monitored items, publishing every {PublishingMs} ms",
            Machine, endpoint?.EndpointUrl, subscription.MonitoredItemCount, device.PublishingMs);
        onConnected();
        events.TryWrite(new ConnectionChanged(Machine, Connected: true));

        try
        {
            return await broken.Task.WaitAsync(ct);
        }
        finally
        {
            // Polite close when we can (stopping, or a failed keep-alive that may still reach the server); never hang on it.
            try { await session.CloseAsync(2000, closeChannel: true, CancellationToken.None); }
            catch (Exception ex) { log.LogDebug("OPC UA {Machine}: close failed ({Error})", Machine, ex.Message); }
        }
    }

    private MonitoredItem? MonitoredItemFor(TagOptions tag, ISession session)
    {
        // "nsu=<uri>;s=..." -> the index this server gives that URI today.
        var nodeId = ExpandedNodeId.ToNodeId(ExpandedNodeId.Parse(tag.NodeId), session.NamespaceUris);
        if (nodeId is null)
        {
            log.LogError("OPC UA {Machine}: tag {Tag} uses namespace {NodeId} that the server does not have", Machine, tag.Name, tag.NodeId);
            return null;
        }

        var item = new MonitoredItem(client.Telemetry)
        {
            DisplayName = tag.Name,
            StartNodeId = nodeId,
            AttributeId = Attributes.Value,
            SamplingInterval = tag.SamplingMs,
            QueueSize = 10,          // changes sampled between two publishes wait here instead of being lost
            DiscardOldest = true,
            // StatusValue: a change of quality (Good -> BadSensorFailure) is reported even when the value stays.
            Filter = new DataChangeFilter
            {
                Trigger = DataChangeTrigger.StatusValue,
                DeadbandType = (uint)(tag.Deadband is > 0 ? DeadbandType.Absolute : DeadbandType.None),
                DeadbandValue = tag.Deadband ?? 0,
            },
        };
        // The library's thread: build the value and hand it over, nothing slow here (spec 2.3, trap 1).
        item.Notification += (mi, _) =>
        {
            foreach (var v in mi.DequeueValues())
                events.TryWrite(new RawValue(Machine, tag.Name, v.Value, Quality(v.StatusCode), SourceTime(v), MachineSources.OpcUa));
        };
        return item;
    }

    private static DataQuality Quality(StatusCode s)
        => StatusCode.IsGood(s) ? DataQuality.Good : StatusCode.IsUncertain(s) ? DataQuality.Uncertain : DataQuality.Bad;

    // Rule G9: the time the value was measured. Some servers leave SourceTimestamp empty: then the server's time,
    // then ours (spec 2.3, trap 3).
    private DateTime SourceTime(DataValue v)
        => v.SourceTimestamp > DateTime.MinValue ? v.SourceTimestamp
         : v.ServerTimestamp > DateTime.MinValue ? v.ServerTimestamp
         : clock.GetUtcNow().UtcDateTime;
}
