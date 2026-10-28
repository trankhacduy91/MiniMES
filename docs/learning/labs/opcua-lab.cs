// Spec M4b step 0: the UaExpert exercise as code. Connects to an OPC UA server, browses the tree, reads one node
// (value + data type + StatusCode + timestamps), then subscribes to the same node twice, without and with an
// absolute deadband, and counts the notifications of each: the deadband is filtered by the server, not the client.
// Server for the lab (Microsoft OPC PLC, no security):
//   docker run -d --name opcplc-lab -p 50000:50000 mcr.microsoft.com/iotedge/opc-plc:latest --pn=50000 --autoaccept --unsecuretransport
//   dotnet run docs/learning/labs/opcua-lab.cs -- [endpoint] [deadband] [seconds] [namespaceUri] [node to subscribe] [more nodes to read...]
//   dotnet run docs/learning/labs/opcua-lab.cs -- opc.tcp://localhost:50000 10 20
// The reflow oven of the simulator (M4b step 1):
//   dotnet run docs/learning/labs/opcua-lab.cs -- opc.tcp://localhost:4840 0.5 20 urn:minimes:simulator:reflow Reflow.PeakTemp Reflow.Counter Reflow.State
#:package OPCFoundation.NetStandard.Opc.Ua.Client@1.5.378.182
#:property PublishAot=false
using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Configuration;

var endpointUrl = args.ElementAtOrDefault(0) ?? "opc.tcp://localhost:50000";
var deadband = double.Parse(args.ElementAtOrDefault(1) ?? "10");
var seconds = int.Parse(args.ElementAtOrDefault(2) ?? "20");
var nsUri = args.ElementAtOrDefault(3) ?? "http://microsoft.com/Opc/OpcPlc/";
var nodeNames = args.Length > 4 ? args[4..] : ["SpikeData", "BadFastUInt1"];

// 1.5.378 marks the old synchronous calls obsolete (2.0 removes them): everything below is the async form,
// and every component receives a telemetry context (logging + metrics) instead of a global logger.
var telemetry = DefaultTelemetry.Create(_ => { });

// ---------- 1. Client configuration: who am I, where are my certificates ----------
var pki = Path.Combine(Path.GetTempPath(), "minimes-opcua-lab", "pki");
var config = new ApplicationConfiguration(telemetry)
{
    ApplicationName = "MiniMes OPC UA lab",
    ApplicationUri = $"urn:{Environment.MachineName}:minimes:opcua-lab",
    ApplicationType = ApplicationType.Client,
    SecurityConfiguration = new SecurityConfiguration
    {
        ApplicationCertificate = new CertificateIdentifier { StoreType = "Directory", StorePath = Path.Combine(pki, "own"), SubjectName = "CN=MiniMes OPC UA lab" },
        TrustedPeerCertificates = new CertificateTrustList { StoreType = "Directory", StorePath = Path.Combine(pki, "trusted") },
        TrustedIssuerCertificates = new CertificateTrustList { StoreType = "Directory", StorePath = Path.Combine(pki, "issuer") },
        RejectedCertificateStore = new CertificateTrustList { StoreType = "Directory", StorePath = Path.Combine(pki, "rejected") },
        AutoAcceptUntrustedCertificates = true,   // lab only: production trusts certificates by hand
    },
    TransportQuotas = new TransportQuotas { OperationTimeout = 15_000 },
    ClientConfiguration = new ClientConfiguration { DefaultSessionTimeout = 60_000 },
};
await config.ValidateAsync(ApplicationType.Client);
// First run creates a self-signed certificate in pki/own (the server sees it even with security None).
await new ApplicationInstance(config, telemetry).CheckApplicationInstanceCertificatesAsync(silent: true);

// ---------- 2. Session ----------
var endpoint = await CoreClientUtils.SelectEndpointAsync(config, endpointUrl, useSecurity: false, telemetry);
Console.WriteLine($"Endpoint  {endpoint.EndpointUrl}  security={endpoint.SecurityMode}/{endpoint.SecurityPolicyUri?.Split('#')[^1]}");
using var session = await new DefaultSessionFactory(telemetry).CreateAsync(config, new ConfiguredEndpoint(null, endpoint, EndpointConfiguration.Create(config)),
    updateBeforeConnect: false, sessionName: "minimes-opcua-lab", sessionTimeout: 60_000,
    identity: new UserIdentity(new AnonymousIdentityToken()), preferredLocales: null);

Console.WriteLine("\nNamespace table (ns=<index> in a NodeId points into this list):");
for (var i = 0; i < session.NamespaceUris.Count; i++) Console.WriteLine($"  ns={i}  {session.NamespaceUris.GetString((uint)i)}");

// ---------- 3. Browse: Objects -> OpcPlc -> Telemetry ----------
Console.WriteLine("\nBrowse Objects (2 levels):");
var browser = new Browser(session)
{
    BrowseDirection = BrowseDirection.Forward,
    ReferenceTypeId = ReferenceTypeIds.HierarchicalReferences,
    IncludeSubtypes = true,
    NodeClassMask = (int)(NodeClass.Object | NodeClass.Variable),
};
foreach (var child in await browser.BrowseAsync(ObjectIds.ObjectsFolder))
{
    Console.WriteLine($"  {child.DisplayName,-24} {child.NodeId}");
    if (child.NodeId.NamespaceIndex == 0) continue;   // standard nodes (Server, Aliases...): not opened here
    foreach (var grand in await browser.BrowseAsync(ExpandedNodeId.ToNodeId(child.NodeId, session.NamespaceUris)))
        Console.WriteLine($"    {grand.DisplayName,-22} {grand.NodeId}");
}

// ---------- 4. Read once: a value always comes with a StatusCode and two timestamps ----------
var ns = (ushort)session.NamespaceUris.GetIndex(nsUri);
var nodes = nodeNames.Select(n => new NodeId(n, ns)).ToList();
var watched = nodes[0];   // the node subscribed below
Console.WriteLine($"\nRead (node ids resolved from the namespace URI: ns={ns}):");
foreach (var id in nodes)
{
    var dv = await session.ReadValueAsync(id);
    Console.WriteLine($"  {id,-34} value={dv.Value,-10} type={dv.WrappedValue.TypeInfo?.BuiltInType} status={StatusCodes.GetBrowseName(dv.StatusCode.CodeBits)} " +
                      $"source={dv.SourceTimestamp:HH:mm:ss.fff} server={dv.ServerTimestamp:HH:mm:ss.fff}");
}

// ---------- 5. Subscribe: server samples, filters (deadband), and pushes ----------
var counts = new int[2];
var subscription = new Subscription(session.DefaultSubscription) { PublishingInterval = 1000, DisplayName = "lab" };
MonitoredItem Watch(int slot, DataChangeFilter? filter)
{
    var item = new MonitoredItem(subscription.DefaultItem)
    {
        StartNodeId = watched, AttributeId = Attributes.Value,
        SamplingInterval = 500, QueueSize = 10, Filter = filter,
    };
    // Library thread: keep it short (the gateway will only push into a Channel here).
    item.Notification += (mi, _) =>
    {
        foreach (var v in mi.DequeueValues())
        {
            Interlocked.Increment(ref counts[slot]);
            if (slot == 1) Console.WriteLine($"  [deadband {deadband}] {v.SourceTimestamp:HH:mm:ss.fff}  {v.Value}  {v.StatusCode}");
        }
    };
    return item;
}
subscription.AddItem(Watch(0, null));
subscription.AddItem(Watch(1, new DataChangeFilter
{
    Trigger = DataChangeTrigger.StatusValue,
    DeadbandType = (uint)DeadbandType.Absolute,
    DeadbandValue = deadband,
}));
session.AddSubscription(subscription);   // add first, then Create: the reverse order fails
await subscription.CreateAsync();

Console.WriteLine($"\nSubscribed to {watched} twice (sampling 500 ms, publishing 1000 ms) for {seconds} s...");
await Task.Delay(TimeSpan.FromSeconds(seconds));
Console.WriteLine($"\nNotifications in {seconds} s: no deadband = {counts[0]}, deadband {deadband} = {counts[1]}");

await subscription.DeleteAsync(silent: true);
await session.CloseAsync();
