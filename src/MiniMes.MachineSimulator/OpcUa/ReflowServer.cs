using Microsoft.Extensions.Logging;
using MiniMes.MachineSimulator.Config;
using Opc.Ua;
using Opc.Ua.Configuration;
using Opc.Ua.Server;

namespace MiniMes.MachineSimulator.OpcUa;

// Spec M4b 2.2: the OPC UA server of one machine (the reflow oven, the "PLC" the gateway reads). Security None and
// auto-accept: dev only (spec section 4, OPC UA security); production uses SignAndEncrypt and hand-trusted certificates.
// "opcua stop" disposes the server and "opcua start" builds a new one (a stopped StandardServer is not restartable);
// the last values are kept here, so a restarted server shows them at once.
public sealed class ReflowServer(string machine, OpcUaOptions options, ITelemetryContext telemetry, ILogger log)
{
    private readonly SemaphoreSlim _gate = new(1, 1);   // start/stop from the console and the host, one at a time
    private OvenServer? _server;
    private ReflowValues? _last;
    private DateTime _lastTime;

    public string Machine => machine;
    public string EndpointUrl => $"opc.tcp://{options.Host}:{options.Port}/";
    public bool IsRunning => _server is not null;

    // Machine loop: every tick. The node manager only touches the nodes whose value changed.
    public void Publish(ReflowValues values, DateTime machineTime)
    {
        (_last, _lastTime) = (values, machineTime);
        _server?.Nodes?.Publish(values, machineTime);
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_server is not null) return;
            var config = await ConfigurationAsync();
            var server = new OvenServer();
            await new ApplicationInstance(config, telemetry).StartAsync(server);
            _server = server;
            if (_last is { } last) server.Nodes?.Publish(last, _lastTime);
            log.LogInformation("{Machine}: OPC UA server at {Endpoint} (namespace {Namespace})", machine, EndpointUrl, ReflowNodeManager.NamespaceUri);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Port taken, certificate folder not writable...: the machine keeps running without its OPC UA side.
            log.LogError(ex, "{Machine}: OPC UA server did not start on {Endpoint}", machine, EndpointUrl);
        }
        finally { _gate.Release(); }
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_server is null) return;
            await _server.StopAsync(ct);   // closes the sessions: the clients see the connection drop
            _server.Dispose();
            _server = null;
            log.LogInformation("{Machine}: OPC UA server stopped", machine);
        }
        finally { _gate.Release(); }
    }

    private async Task<ApplicationConfiguration> ConfigurationAsync()
    {
        // Next to the exe, not in the working folder: started from another folder, the server would make a new
        // certificate and every client that trusted the old one would have to trust it again (spec 2.2, trap 3).
        var pki = Path.Combine(AppContext.BaseDirectory, "pki");
        var config = new ApplicationConfiguration(telemetry)
        {
            ApplicationName = $"MiniMes simulator {machine}",
            ApplicationUri = $"urn:{System.Net.Dns.GetHostName()}:minimes:simulator:{machine}",
            ProductUri = "urn:minimes:simulator",
            ApplicationType = ApplicationType.Server,
            SecurityConfiguration = new SecurityConfiguration
            {
                ApplicationCertificate = new CertificateIdentifier
                {
                    StoreType = CertificateStoreType.Directory,
                    StorePath = Path.Combine(pki, "own"),
                    SubjectName = $"CN=MiniMes simulator {machine}, O=MiniMes",
                },
                TrustedPeerCertificates = new CertificateTrustList { StoreType = CertificateStoreType.Directory, StorePath = Path.Combine(pki, "trusted") },
                TrustedIssuerCertificates = new CertificateTrustList { StoreType = CertificateStoreType.Directory, StorePath = Path.Combine(pki, "issuer") },
                RejectedCertificateStore = new CertificateTrustList { StoreType = CertificateStoreType.Directory, StorePath = Path.Combine(pki, "rejected") },
                AutoAcceptUntrustedCertificates = true,
            },
            TransportQuotas = new TransportQuotas { OperationTimeout = 15_000 },
            ServerConfiguration = new ServerConfiguration
            {
                // The URL the server announces: the client must be able to resolve this host (spec 2.2, trap 1).
                BaseAddresses = { EndpointUrl },
                SecurityPolicies = { new ServerSecurityPolicy { SecurityMode = MessageSecurityMode.None, SecurityPolicyUri = SecurityPolicies.None } },
                UserTokenPolicies = { new UserTokenPolicy(UserTokenType.Anonymous) },
                MinRequestThreadCount = 5,
                MaxRequestThreadCount = 20,
                MaxQueuedRequestCount = 200,
                MaxSessionCount = 20,
                // No Local Discovery Server here. Left on, the server registers itself every few seconds with the LDS
                // at opc.tcp://localhost:4840, the LDS's standard port, so on this port it calls itself and fails.
                MaxRegistrationInterval = 0,
                // "opcua stop" = the PLC is switched off: gone at once. The default (5 s) first announces ServerState.Shutdown
                // and keeps accepting sessions meanwhile, so a client reconnects into a dying server and the machine flaps
                // Offline/online/Offline (M4b step 6). The gateway still handles that announcement for real servers.
                ShutdownDelay = 0,
            },
        };
        await config.ValidateAsync(ApplicationType.Server);
        // First start: creates a self-signed certificate in pki/own.
        await new ApplicationInstance(config, telemetry).CheckApplicationInstanceCertificatesAsync(silent: true);
        return config;
    }

    // StandardServer with our node manager next to the built-in one (namespace 0, the Server object...).
    private sealed class OvenServer : StandardServer
    {
        public ReflowNodeManager? Nodes { get; private set; }

        protected override MasterNodeManager CreateMasterNodeManager(IServerInternal server, ApplicationConfiguration configuration)
        {
            Nodes = new ReflowNodeManager(server, configuration);
            return new MasterNodeManager(server, configuration, null, Nodes);
        }

        protected override ServerProperties LoadServerProperties() => new()
        {
            ManufacturerName = "MiniMes",
            ProductName = "MiniMes machine simulator",
            ProductUri = "urn:minimes:simulator",
            SoftwareVersion = Utils.GetAssemblySoftwareVersion(),
            BuildNumber = Utils.GetAssemblyBuildNumber(),
            BuildDate = Utils.GetAssemblyTimestamp(),
        };
    }
}

// OPC UA stack logs go through the host's logger factory (Serilog-free console here), filtered in Program.cs.
public sealed class HostTelemetry(ILoggerFactory loggers) : TelemetryContextBase(loggers);
